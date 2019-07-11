namespace VSharp.Core

#nowarn "69"

open VSharp
open VSharp.Core.Types
open VSharp.Core.Types.Constructor
open System.Collections.Immutable

module internal Memory =
    open Terms
    open Merging
    open State

// ------------------------------- Primitives -------------------------------

    let private pointer = persistent<int>(always 0, id)
    let private timestamp = persistent<timestamp>(always Timestamp.zero, id)
    let freshAddress() =
        pointer.Mutate(pointer.Value + 1)
        pointer.Value
    let tick() =
        timestamp.Mutate(timestamp.Value |> Timestamp.inc)
        timestamp.Value
    let reset() =
        pointer.Reset()
        timestamp.Reset()
    let saveConfiguration() =
        pointer.Save()
        timestamp.Save()
    let restore() =
        pointer.Restore()
        timestamp.Restore()

    let npe mtd state = State.createInstance mtd typeof<System.NullReferenceException> [] state

    let private npeTerm mtd state _ =
        let exn, state = npe mtd state
        Error mtd exn, state

    let rec private referenceSubLocations locations term =
        match term.term with
        | Error _ -> term
        | Ref(tl, path) -> Ref term.metadata tl (List.append path locations)
        | Ptr(tl, path, typ, shift) -> AnyPtr term.metadata tl (List.append path locations) typ shift
        | GuardedValues(gs, vs) -> vs |> List.map (referenceSubLocations locations) |> List.zip gs |> Union term.metadata
        | _ -> internalfailf "expected reference, but got %O" term

    let referenceArrayLowerBound arrayRef (indices : term) =
        referenceSubLocations [ArrayLowerBound indices] arrayRef

    let referenceArrayLength arrayRef (indices : term) =
        referenceSubLocations [ArrayLength indices] arrayRef

// ------------------------------- Traversal -------------------------------

    // TODO: path should NOT be accumulated, but taken from key metainfo
    let inline private foldHeapLocationsRec folder =
        Heap.foldFQL (fun acc subloc -> folder acc (getFQLOfKey subloc |> snd |> List.tryLast))

    type private foldSubLocationAccWrapper<'acc, 'loc, 'pathSegment> =
        { acc : 'acc; loc : 'loc; path : 'pathSegment list }

    let rec private foldSubLocations folder fillHoles fillPath mergeAcc acc segment cell =
        let foldHeap target acc =
            foldHeapLocationsRec (foldSubLocations folder fillHoles fillPath mergeAcc) acc target
        let acc' = lazy (
            match segment with
            | Some segment ->
                { acc with path = fillPath segment :: acc.path }
            | None -> acc)
        let changePath acc' = { acc' with path = acc.path } 
        let apply term =
            match term.term with
            | Struct(contents, _) ->
                acc'.Force()
             |> foldHeap contents
             |> changePath
            | Array(_, _, lower, _, contents, lengths, _) ->
                acc'.Force()
             |> foldHeap lower
             |> foldHeap lengths
             |> foldHeap contents
             |> changePath
            | _ -> folder acc segment cell
        commonGuardedErroredApply apply apply (cell.value) (fun gvs ->
            let gs, vs = List.unzip gvs
            let gs' = List.map fillHoles gs
            let acc' = List.head vs
            { acc' with acc =  mergeAcc gs' (List.map (fun acc -> acc.acc) vs) })

    let private foldHeapLocations folder fillHoles fillKey fillPath mergeAcc acc heap = // TODO: get rid of typeOf
        Heap.fold (fun acc loc cell -> (foldSubLocations (fun acc -> folder acc (typeOf cell.value)) fillHoles fillPath mergeAcc ({ acc = acc; loc = fillKey loc; path = [] }) None cell).acc) acc heap

    let private foldStackLocations folder fillHoles fillPath mergeAcc acc stack =
        stackFold (fun acc loc cell -> (foldSubLocations (fun acc -> folder acc (typeOf cell.value)) fillHoles fillPath mergeAcc ({ acc = acc; loc = loc; path = [] }) None cell).acc) acc stack

// ------------------------------- Instantiation (lazy & default) -------------------------------

    [<StructuralEquality;NoComparison>]
    type private lazyInstantiation<'a when 'a : equality> =
        {location : term; heap : 'a generalizedHeap option; extractor : TermExtractor; typeExtractor : TypeExtractor}
        interface IExtractingSymbolicConstantSource with
            override x.SubTerms = Seq.singleton x.location
            override x.WithExtractor e = {x with extractor = e} :> IExtractingSymbolicConstantSource
        interface IExtractingSymbolicTypeSource with
            override x.WithTypeExtractor e = {x with typeExtractor = e} :> IExtractingSymbolicTypeSource
            override x.TypeCompose ctx state =
                (x :> IStatedSymbolicConstantSource).Compose ctx state |> typeOf |> x.typeExtractor.TypeExtract
            override x.TypeEquals other =
                match other with
                | :? lazyInstantiation<'a> as li -> x.location = li.location
                | _ -> false
    let (|LazyInstantiation|_|) (src : ISymbolicConstantSource) =
        match src with
        | :? lazyInstantiation<'a> as li -> Some(li.location, li.heap, li.extractor :? IdTermExtractor)
        | _ -> None

    let (|LazyInstantiationEpsilon|_|) (src : ISymbolicConstantSource) = // for None case of ground heap
        let getLocation (li : 'a lazyInstantiation) =
            match li with
            | { heap = None } -> Some(li.location)
            | _ -> None
        match src with
        | :? lazyInstantiation<obj> as li -> getLocation li
        | :? lazyInstantiation<termType> as li -> getLocation li
        | :? lazyInstantiation<term> as li -> getLocation li // TODO: generic shape pattern matching doesn't work in F#!
        | _ -> None                                          // TODO: there should be more cases

    let private mkStruct metadata time isStatic mkField dotNetType typ fql =
        let mkFieldKey typ name = StructField(name, typ)
        let fields = Types.fieldsOf dotNetType isStatic
        let contents =
            fields
            |> FSharp.Collections.Array.fold (fun acc (name, typ) ->
                let typ = wrapReferenceType typ
                let fql' = StructField(name, typ) |> addToOptionFQL fql
                let value = mkField metadata name typ fql'
                Heap.add (makePathKey fql (mkFieldKey typ) name) { value = value; created = time; modified = time } acc) Heap.empty
        Struct metadata contents typ

    let rec private defaultOf time metadata typ fql =
        match typ with
        | Bool -> makeFalse metadata
        | Numeric t when t.IsEnum -> CastConcrete (System.Activator.CreateInstance t) t metadata
        | Numeric t -> CastConcrete 0 t metadata
        | Func _
        | Reference _
        | InterfaceType _ -> makeNullRef metadata
        | TypeVariable _ ->
            Common.statelessConditionalExecution
                (fun k -> k <| Common.isValueType metadata typ)
                (fun k -> k <| Struct metadata Heap.empty typ)
                (fun k -> k <| makeNullRef metadata)
                Merging.merge Merging.merge2Terms id
        | StructType(dotNetType, _) ->
            mkStruct metadata time false (fun m _ t -> defaultOf time m t) dotNetType typ fql
        | Pointer typ -> makeNullPtr metadata typ
        | _ -> __notImplemented__()

    let mkDefault metadata typ fql =
        defaultOf (tick()) metadata typ fql

    let mkDefaultStruct metadata targetType fql =
        let dnt = toDotNetType targetType
        let time = tick()
        mkStruct metadata time false (fun m _ t -> defaultOf time m t) dnt targetType fql

    let private makeSymbolicHeapReference metadata (source : IExtractingSymbolicConstantSource) name typ construct =
        let source' = source.WithExtractor(Pointers.HeapAddressExtractor())
        let constant = Constant metadata name source' pointerType
        construct metadata constant typ typ []

    let private makeSymbolicOveralArrayLength metadata (source : IExtractingSymbolicConstantSource) arrayName =
        Constant metadata (sprintf "|%s|" arrayName) (source.WithExtractor(Arrays.LengthExtractor())) lengthType

    let private makeSymbolicArrayRank metadata (source : IExtractingSymbolicConstantSource) arrayName =
        Constant metadata ("RankOf_%s" + arrayName) (source.WithExtractor(Arrays.RankExtractor())) lengthType

    let private makeSymbolicArrayLowerBound metadata time name fql heap =
        match Options.ExplorationMode() with
        | TrustConventions -> defaultOf time metadata lengthType <| Some fql
        | CompleteExploration ->
            Constant metadata name {location = makeFQLRef metadata fql; heap = heap; extractor = IdTermExtractor(); typeExtractor = IdTypeExtractor()} lengthType

    let private makeSymbolicArrayLength metadata name fql heap =
        Constant metadata name {location = makeFQLRef metadata fql; heap = heap; extractor = IdTermExtractor(); typeExtractor = IdTypeExtractor()} lengthType

    let private makeSymbolicArrayLowerBounds metadata (source : IExtractingSymbolicConstantSource) arrayName dimension fql =
        match source with
        | :? lazyInstantiation<term> as liSource ->
            match Options.ExplorationMode() with
            | TrustConventions -> Arrays.zeroLowerBounds metadata dimension fql
            | CompleteExploration ->
                let idOfBound i = sprintf "%s.%i_LowerBound" arrayName i
                let mkLowerBound i = Constant metadata (idOfBound i) {liSource with location = referenceArrayLowerBound liSource.location (makeNumber metadata i)} lengthType
                Seq.foldi (fun h i l -> Heap.add (makePathIndexKey metadata ArrayLowerBound i fql) { value = l; created = Timestamp.zero; modified = Timestamp.zero } h) Heap.empty (Seq.init dimension mkLowerBound)
        | _ -> __notImplemented__()

    let private makeSymbolicArrayLengths metadata (source : IExtractingSymbolicConstantSource) arrayName dimension fql =
        match source with
        | :? lazyInstantiation<term> as liSource ->
            let idOfLength i = sprintf "%s.%i_Length" arrayName i
            let mkLength i = Constant metadata (idOfLength i) {liSource with location = referenceArrayLength liSource.location (makeNumber metadata i)} lengthType
            let lengths = Seq.init dimension mkLength
            let length = Seq.reduce (mul metadata) lengths
            Seq.foldi (fun h i l -> Heap.add (makePathIndexKey metadata ArrayLength i fql) { value = l; created = Timestamp.zero; modified = Timestamp.zero } h) Heap.empty lengths, length
        | _ -> __notImplemented__()

    let private makeSymbolicArray metadata source dimension elemTyp typ arrayName fql =
        let arrayConstant = Constant metadata arrayName source typ
        let instantiator = [True, LazyInstantiator(arrayConstant, elemTyp)]
        let lowerBound, arrayLengths, arrayLength, dim =
            let makeConcrete d =
                let lb = makeSymbolicArrayLowerBounds metadata source arrayName d fql
                let al, length = makeSymbolicArrayLengths metadata source arrayName d fql
                lb, al, length, makeNumber metadata d
            match dimension with
            | Vector -> makeConcrete 1
            | ConcreteDimension d -> makeConcrete d
            | SymbolicDimension _ ->
                let length = makeSymbolicOveralArrayLength metadata source arrayName
                Heap.empty, Heap.empty, length, makeSymbolicArrayRank metadata source arrayName
        Array metadata dim arrayLength lowerBound instantiator Heap.empty arrayLengths typ

    let private makeSymbStrArray metadata time (source : IExtractingSymbolicConstantSource) strLength typ name arrayFQL =
        let arrayConstant = Constant metadata name source typ
        let instantiator = [True, LazyInstantiator(arrayConstant, Char)]
        Strings.makeStringArray metadata time strLength instantiator Heap.empty Char arrayFQL

    let private makeSymbolicString mtd time (source : IExtractingSymbolicConstantSource) strName fql =
        match source with
        | :? lazyInstantiation<term> as liSource ->
            let makeSymbolicStringField key t makeField =
                let ref = referenceSubLocations [StructField(key, t)] liSource.location
                makeField {liSource with location = ref} t
            let lengthName = sprintf "%s.m_StringLength" strName
            let length = makeSymbolicStringField Strings.strLength lengthType (Constant mtd lengthName)
            let arrayFQL = Strings.makeArrayFQL fql
            let array = makeSymbolicStringField Strings.strArray (ArrayType (Char, Vector)) (fun src t -> makeSymbStrArray mtd time src length t (sprintf "%s.m_FirstChar" strName) arrayFQL)
            Strings.makeStringOfFields mtd time length array arrayFQL fql
        | _ -> __notImplemented__()

    let makeSymbolicInstance metadata time source typeSource name fql = function
        | Pointer typ -> makeSymbolicHeapReference metadata source name typ (fun mtd tl bTyp sTyp path -> HeapPtr mtd tl bTyp sTyp path sTyp)
        | Reference typ -> makeSymbolicHeapReference metadata source name typ HeapRef
        | t when Types.isPrimitive t || Types.isFunction t -> Constant metadata name source t
        | StringType -> makeSymbolicString metadata time source name fql
        | StructType _ // TODO: initialize all fields of struct symbolicly (via mkStruct). Warning: `source` should be updated!
        | InterfaceType _
        | TypeVariable _
        | ClassType _ as t ->
            let t =
                match fql with
                | Some(TopLevelStatics t, []) -> t
                | _ -> Types.Variable.fromTermType name typeSource t
            Struct metadata Heap.empty t
        | ArrayType(_, d) as t ->
            let t = Types.Variable.fromTermType name typeSource t
            let e = ArrayTypeExtractor().TypeExtract t
            makeSymbolicArray metadata source d e t name fql
        | Void -> Nop
        | _ -> __notImplemented__()

    let private genericLazyInstantiator<'a when 'a : equality> metadata heap time fql typ () =
        let source : 'a lazyInstantiation = {location = makeFQLRef metadata fql; heap = heap; extractor = IdTermExtractor(); typeExtractor = IdTypeExtractor()}
        makeSymbolicInstance metadata time source source (nameOfLocation fql) (Some fql) typ

    let () =
        State.genericLazyInstantiator <- fun mtd -> genericLazyInstantiator mtd None

    let private arrayElementLazyInstantiator metadata instantiator typ heap time fql idx = function
        | DefaultInstantiator(_, concreteType) -> fun () -> defaultOf time metadata (typ |?? concreteType) <| Some fql
        | LazyInstantiator(array, concreteType) -> instantiator |?? fun () ->
            let id = sprintf "%s[%s]" (toString array) (idx.term.IndicesToString())
            let source = {location = makeFQLRef metadata fql; heap = heap; extractor = IdTermExtractor(); typeExtractor = IdTypeExtractor()}
            makeSymbolicInstance metadata time source source id (Some fql) concreteType
    let private arrayLowerBoundLazyInstantiator metadata instantiator _ heap time fql (idx : term) = function
        | DefaultInstantiator(_, _) -> fun () -> defaultOf time metadata lengthType <| Some fql
        | LazyInstantiator(array, _) -> instantiator |?? fun () ->
            let name = sprintf "%O.%s_LowerBound" array (idx.term.IndicesToString())
            makeSymbolicArrayLowerBound metadata time name fql heap

    let private arrayLengthLazyInstantiator metadata instantiator _ heap _ fql (idx : term) = function
        | DefaultInstantiator(_, _) -> fun () ->
            // In case when array was allocated during the interpretation (and thus has default instantiator) lengths by
            // all dimensions are known (they can be symbolic, but still defined). If this code triggers then we have
            // requested length by a greater dimension than our array has. That can happen in case of comparison of array
            // lengths when arrays have different ranks. In that case we consider lengths in all other dimensions equal to 1.
            makeNumber metadata 1
        | LazyInstantiator(array, _) -> instantiator |?? fun () ->
            let name = sprintf "%O.%s_Length" array (idx.term.IndicesToString())
            makeSymbolicArrayLength metadata name fql heap

    let private staticMemoryLazyInstantiator metadata typ () =
        Struct metadata Heap.empty typ

    let private selectLazyInstantiator<'a when 'a : equality> metadata (heap : 'a generalizedHeap option) time fql typ =
        match fql with
        | _, (_::_ as path) when isArrayLengthSeg <| List.last path -> fun () -> makeSymbolicArrayLength metadata (nameOfLocation fql + "_Length") fql heap
        | _, (_::_ as path) when isArrayLowerBoundSeg <| List.last path -> fun () -> makeSymbolicArrayLowerBound metadata time (nameOfLocation fql + "_LowerBound") fql heap
        | TopLevelStatics _, [] -> staticMemoryLazyInstantiator metadata typ
        | _ -> genericLazyInstantiator<'a> metadata heap time fql typ

// ------------------------------- Locations comparison -------------------------------

    type private 'key pointerInfo = { location : 'key; fullyQualifiedLocation : fql; typ : termType; time : timestamp; path : pathSegment list }

    let private canPoint mtd keyCompare pointerAddr pointerType locationAddr locationValue =
        // TODO: what if locationType is Null?
        let addrEqual = keyCompare mtd pointerAddr locationAddr
        let typeSuits v = Common.typesEqual mtd (typeOf v) pointerType
        let typeEqual =
            match locationValue.term with
            | Union gvs ->
                gvs |> List.map (fun (g, v) -> (g, typeSuits v)) |> Merging.merge
            | _ -> typeSuits locationValue
        if isConcrete addrEqual then addrEqual else addrEqual &&& typeEqual

    let private findSuitableLocations<'key when 'key : equality> mtd h keyCompare contextList mapper (ptr : 'key pointerInfo) =
        let filterMapKey (k : heapKey<'key, fql>, cell) =
            let k, v = List.fold mapper (k.key, cell.value) contextList
            let cell = {cell with value = v}
            let guard = canPoint mtd keyCompare ptr.location ptr.typ k v
            match guard with
            | False -> None
            | _ -> Some(guard, k, cell)
        let gvs = h |> Heap.toSeq |> List.ofSeq |> List.choose filterMapKey
        let baseGvs, restGvs = gvs |> List.partition (fst3 >> isTrue)
        let baseGvs = List.map (fun (_, a, v) -> a, v) baseGvs
        assert(List.length baseGvs <= 1)
        List.tryHead baseGvs, restGvs

// ------------------------------- Primitive read/write -------------------------------

    let private stackDeref time instantiateLazy state location =
        if isAllocatedOnStack state location then
            (readStackLocation state location, state)
        else
            let lazyInstance = {value = instantiateLazy(); created = time; modified = time }
            (lazyInstance, writeStackLocation state location lazyInstance)

    let private writeHeap time guard h addr newValue fql =
        assert(Heap.contains addr h)
        let oldCell = Heap.find addr h
        let cell = merge2Cells guard !!guard { oldCell with value = newValue; modified = time } oldCell
        Heap.add (makeKey addr <| Some fql) cell h

    let private isTopLevelHeapConcreteAddr = function
        | TopLevelHeap(addr, _, _), [] when isConcrete addr -> true
        | _ -> false

// ------------------------------- Core -------------------------------

    let rec private accessHeap<'a, 'key when 'a : equality and 'key : equality> read restricted metadata (groundHeap:'a generalizedHeap option) guard update (h : heap<'key, term, fql>) time keyCompare contextList mapper lazyInstantiator ptr =
        let accessRec gvas lazyValue h =
            let accessLocation (h, minCreatedTime, maxModifeiedTime) (guard', addr, cell) =
                let guard'' = guard &&& guard'
                let accessedCell, newBaseValue = accessTerm read metadata groundHeap guard'' update contextList lazyInstantiator ptr.time ptr.fullyQualifiedLocation ptr.path cell
                let h' = if read || cell.value = newBaseValue then h else writeHeap accessedCell.modified guard'' h addr newBaseValue ptr.fullyQualifiedLocation
                (guard'', accessedCell.value), (h', min minCreatedTime accessedCell.created, max maxModifeiedTime accessedCell.modified)
            let gvs, (h', minCreated, maxModified) = List.mapFold accessLocation (h, Timestamp.infinity, Timestamp.zero) gvas
            let minCreated, maxModified = Option.fold (fun (c, m) _ -> min time c, max time m) (minCreated, maxModified) lazyValue
            { value = merge (optCons gvs lazyValue); created = minCreated; modified = maxModified }, h'
        if Heap.contains ptr.location h then
            accessRec [(makeTrue metadata, ptr.location, Heap.find ptr.location h)] None h
        else
            let baseGav, restGavs = findSuitableLocations metadata h keyCompare contextList mapper ptr
            match baseGav with
            | None when read && restricted ->
                // TODO: undefined behaviour detected!
                __notImplemented__()
            | None ->
                let baseGuard = restGavs |> List.map (fst3 >> (!!)) |> conjunction metadata
                let lazyValue =
                    if read && isTopLevelHeapConcreteAddr ptr.fullyQualifiedLocation && List.isEmpty contextList then Union metadata []
                    else lazyInstantiator |?? genericLazyInstantiator metadata groundHeap time ptr.fullyQualifiedLocation ptr.typ |> eval
                let baseCell = { value = lazyValue; created = time; modified = time}
                let gavs = if read then restGavs else (baseGuard, ptr.location, baseCell)::restGavs
                let lv = if read then Some(baseGuard, lazyValue) else None
                let h = if read then h else h.Add(makeKey ptr.location <| Some ptr.fullyQualifiedLocation, baseCell)
                accessRec gavs lv h
            | Some(a, v) -> accessRec ((makeTrue metadata, a, v)::restGavs) None h

    and private accessTerm read metadata (groundHeap: 'a generalizedHeap option) guard (update : term -> timestamp -> term * timestamp) contextList lazyInstantiator ptrTime ptrFql path ({created = c; modified = m} as cell) =
        let internalMerge gvs =
            let cells, newVs = List.fold (fun (cells, newVs) (g, (c, v)) -> (g, c)::cells, (g, v)::newVs) ([], []) gvs
            mergeCells cells, merge newVs
        let doAccess term =
            match path with
            | [] ->
                let newTerm, newModified = update term m
                { cell with value = newTerm; modified = newModified }, newTerm
            | location :: path' ->
                match term.term with
                | Struct(fields, t) ->
                    let fql' = addToFQL location ptrFql
                    match location with
                    | StructField(name, typ) ->
                        let instantiator = if read then lazyInstantiator else Some <| genericLazyInstantiator<'a> term.metadata groundHeap m fql' typ
                        let ptr' = { location = name; fullyQualifiedLocation = fql'; typ = typ; time = ptrTime; path = path' }
                        let mapper (k, term) (ctx, s) = k, fillHoles ctx s term
                        let resultCell, newFields = accessHeap<'a, string> read false metadata groundHeap guard update fields c compareStringKey contextList mapper instantiator ptr'
                        resultCell, Struct term.metadata newFields t
                    | _ -> __unreachable__()
                | Array(dimension, length, lower, constant, contents, lengths, arrTyp) ->
                    let fql' = addToFQL location ptrFql
                    let newHeap heap instor keyCompare ptr = accessHeap<'a, term> read false metadata groundHeap guard update heap c keyCompare contextList termKeyMapper (Some instor) ptr
                    let makePtr key typ = { location = key; fullyQualifiedLocation = fql'; typ = typ; time = ptrTime; path = path' }
                    let makeInstantiator key instantiator =
                        let realInstantiator, targetType = if read then lazyInstantiator, Some(typeOfPath path) else None, None
                        let doJob = lazy(guardedMap (fun c -> instantiator term.metadata realInstantiator targetType groundHeap m fql' key c ()) constant)
                        doJob.Force
                    match location with
                    | ArrayIndex(key, typ) ->
                        let instantiator = makeInstantiator key arrayElementLazyInstantiator
                        let resultCell, newContents = newHeap contents instantiator Arrays.equalsArrayIndices <| makePtr key typ
                        resultCell, Array term.metadata dimension length lower constant newContents lengths arrTyp
                    | ArrayLength key ->
                        let instantiator = makeInstantiator key arrayLengthLazyInstantiator
                        let resultCell, newLengths = newHeap lengths instantiator fastNumericCompare <| makePtr key lengthType
                        resultCell, Array term.metadata dimension length lower constant contents newLengths arrTyp
                    | ArrayLowerBound key ->
                        let instantiator = makeInstantiator key arrayLowerBoundLazyInstantiator
                        let resultCell, newLower = newHeap lower instantiator fastNumericCompare <| makePtr key lengthType
                        resultCell, Array term.metadata dimension length newLower constant contents lengths arrTyp
                    | _ -> __unreachable__()
                | t -> internalfailf "expected complex type, but got %O" t
        commonGuardedErroredApply doAccess (withFst cell) cell.value internalMerge

    and private compareStringKey mtd loc key = makeBool (loc = key) mtd

    and private removeTypeVariable = function // TODO: temporary hack! Refactor this!
        | ArrayType(TypeVariable(Implicit(_, _, typ)), d) -> ArrayType(typ, d)
        | TypeVariable(Implicit(_, _, typ)) -> typ
        | typ -> typ

    and private changeType typ' = function
        | StructField(f, _) -> StructField(f, typ')
        | ArrayIndex(i, _) -> ArrayIndex(i, typ')
        | ArrayLowerBound _
        | ArrayLength _ as seg -> seg

    and private readTerm mtd (_ : bool) cell fql typ =
        let segment = snd fql |> List.last
        let typ = removeTypeVariable typ
        let path = [changeType typ segment]
        let lazyInstor = genericLazyInstantiator mtd None Timestamp.zero fql typ
        accessTerm true mtd None (makeTrue mtd) makePair [] (Some lazyInstor) Timestamp.infinity fql path cell |> fst

    and private commonHierarchicalStackAccess read update metadata state location path =
        let firstLocation = TopLevelStack location, []
        let time = frameTime state location
        let cell, _ = stackDeref time (fun () -> (stackLazyInstantiator state time location).value) state location
        let termLazyInstantiator = if read && not (List.isEmpty path) then genericLazyInstantiator metadata None time (TopLevelStack location, path) (typeOfPath path) else __unreachable__
        let accessedCell, newBaseValue = accessTerm read metadata None (makeTrue metadata) update [] (Some termLazyInstantiator) time firstLocation path cell
        let newState = if read || cell.value = newBaseValue then state else writeStackLocation state location { cell with value = newBaseValue; modified = accessedCell.modified }
        accessedCell.value, newState

    and private termKeyMapper (k, v) (ctx, s) = fillHoles ctx s k, fillHoles ctx s v

    and private commonHierarchicalHeapAccess read restricted update metadata groundHeap heap contextList lazyInstantiator addr typ path time =
        let firstLocation = TopLevelHeap(addr, typ, typ), []
        let typ' = if List.isEmpty path then typ else typeOfPath path
        let readInstor = lazyInstantiator |?? selectLazyInstantiator<term> metadata groundHeap time.v (TopLevelHeap(addr, typ, typ'), path) typ'
        let lazyInstantiator = if read then Some readInstor else None
        let ptr = {location = addr; fullyQualifiedLocation = firstLocation; typ = typ; time = time.v; path = path}
        accessHeap<term, term> read restricted metadata groundHeap (makeTrue metadata) update heap Timestamp.zero fastNumericCompare contextList termKeyMapper lazyInstantiator ptr

    and readHeap metadata restricted heap key typ =
        let typ' = removeTypeVariable typ
        commonHierarchicalHeapAccess true restricted makePair metadata None heap [] None key typ' [] {v = Timestamp.infinity} |> fst

    and private commonHierarchicalStaticsAccess read restricted update metadata groundHeap statics contextList lazyInstantiator typ path =
        let typ' = if List.isEmpty path then typ else typeOfPath path
        let lazyInstantiator =
            if read then
                let readInstor = lazyInstantiator |?? selectLazyInstantiator<termType> metadata groundHeap Timestamp.infinity (TopLevelStatics typ, path) typ'
                Some readInstor
            else None
        let ptr = {location = typ; fullyQualifiedLocation = TopLevelStatics typ, []; typ = typ; time = Timestamp.infinity; path = path}
        let mapper (k, v) (ctx, s) = substituteTypeVariables ctx s k, fillHoles ctx s v
        accessHeap<termType, termType> read restricted metadata groundHeap (makeTrue metadata) update statics Timestamp.zero Common.typesEqual contextList mapper lazyInstantiator ptr

    and readStatics metadata restricted statics key _ =
        commonHierarchicalStaticsAccess true restricted makePair metadata None statics [] None key [] |> fst

    and mutateStack metadata state location path time value =
        commonHierarchicalStackAccess false (fun _ _ -> value, time) metadata state location path |> snd

    and private mutateHeap restricted metadata h loc typ path time value =
        commonHierarchicalHeapAccess false restricted (fun _ _ -> value, time) metadata None h [] None loc typ path {v = time} |> snd

    and private mutateStatics restricted metadata statics location _ path time value =
        commonHierarchicalStaticsAccess false restricted (fun _ _ -> value, time) metadata None statics [] None location path |> snd

    and private independent<'a when 'a : equality> (exploredRecursiveIds : ImmutableHashSet<IFunctionIdentifier>) (read : ImmutableHashSet<IFunctionIdentifier> -> state -> term * 'a generalizedHeap) funcId location : bool =
        exploredRecursiveIds.Contains funcId ||
        let exploredRecursiveIds = exploredRecursiveIds.Add funcId
        match Database.querySummary funcId with
        | Some summary ->
            let t, _ = read exploredRecursiveIds summary.state
            match t.term with
            | Constant(_, LazyInstantiationEpsilon(location'), _) when location = location' -> true
            |_ -> false
        | None -> false

    and private accessGeneralizedHeapRec<'a when 'a : equality> (exploredIds : ImmutableHashSet<IFunctionIdentifier>) unlucky contextList lazyInstantiator read readHeap (getter : state -> 'a generalizedHeap) location accessDefined = function
        | Defined(r, h) ->
            let result, heap = accessDefined contextList lazyInstantiator None r h
            result, Defined r heap
        | Merged ghs ->
            let foldFunc (g, h) (gvs, gs, hs) =
                let v, h' = accessGeneralizedHeapRec exploredIds unlucky contextList lazyInstantiator read readHeap getter location accessDefined h
                ((g, v)::gvs, g::gs, h'::hs)
            let gvs, gs, hs = List.foldBack foldFunc ghs ([], [], [])
            merge gvs, mergeGeneralizedHeaps readHeap gs hs
        | Mutation(h, h') as m ->
            let result, h'' = accessDefined contextList lazyInstantiator (Some h) false h'
            if read then
                let accessH = lazy(accessGeneralizedHeapRec exploredIds unlucky contextList lazyInstantiator read readHeap getter location accessDefined h |> fst)
                let simplifyInstantiated term =
                    match term.term with
                    | Constant(_, LazyInstantiation(loc, Some heap, _), _) when loc = location && heap = h ->
                        accessH.Force()
                    | _ -> term
                Substitution.substitute simplifyInstantiated id result, m
            else
                result, Mutation(h, h'')
        | Composition(_, _, Defined _) ->
            internalfail "composition with the defined heap should not be met, it must be simplified to a simple mutation!"
        | Composition(s, ctx, h) as heap when read ->
            let unlucky _ = unlucky heap  // if simplification of 'h' fails then we should return 'heap' instead of 'h'
            let lazyInstantiator' = lazy(accessGeneralizedHeapRec exploredIds unlucky contextList lazyInstantiator read readHeap getter location accessDefined (getter s) |> fst)
            accessGeneralizedHeapRec exploredIds unlucky ((ctx, s) :: contextList) (Some lazyInstantiator'.Force) read readHeap getter location accessDefined h
        | RecursiveApplication(f, _, _) as h
                when read && independent exploredIds
                                 (fun ids s -> accessGeneralizedHeapWithIDs ids read readHeap getter location accessDefined (getter s)) f location ->
            let r, _ = accessDefined contextList lazyInstantiator None false Heap.empty
            r, h
        | Composition _
        | RecursiveApplication _
        | HigherOrderApplication _ as h -> unlucky h contextList
        | _ -> __unreachable__()

    and private accessGeneralizedHeapWithIDs exploredIds (read : bool) readHeap getter location accessDefined =
        let unlucky h contextList =
            let r, e = accessDefined contextList None (Some h) false Heap.empty
            r, if read then h else Mutation(h, e)
        accessGeneralizedHeapRec<'a> exploredIds unlucky [] None read readHeap getter location accessDefined

    and private accessGeneralizedHeap read = accessGeneralizedHeapWithIDs ImmutableHashSet.Empty read

    and private hierarchicalAccess validate read actionNull updateDefined metadata =
        let doAccess state term =
            match term.term with
            | Ref(NullAddress, _) -> actionNull metadata state Null
            | Ref(TopLevelStack location, path) ->
                commonHierarchicalStackAccess read updateDefined metadata state location path
            | Ref(TopLevelHeap(addr, bT, _), path) ->
                let doRead state k =
                    let accessDefined contextList lazyInstantiator groundHeap r h = //TODO: get rid of time
                        let cell, heap = commonHierarchicalHeapAccess read r updateDefined metadata groundHeap h contextList lazyInstantiator addr bT path {v = Timestamp.zero}
                        cell.value, heap
                    let result, h' = accessGeneralizedHeap read (readHeap metadata) heapOf term accessDefined (heapOf state)
                    k (result, withHeap state h')
                if validate then
                    Common.statedConditionalExecution state
                        (fun state k -> k (Pointers.isZeroAddress metadata addr, state))
                        (fun state k -> k (actionNull metadata state bT))
                        doRead
                        merge merge2Terms id id
                else doRead state id
            | Ref(TopLevelStatics location, path) ->
                let accessDefined contextList lazyInstantiator groundHeap r h =
                    let cell, statics = commonHierarchicalStaticsAccess read r updateDefined metadata groundHeap h contextList lazyInstantiator location path
                    cell.value, statics
                let result, m' = accessGeneralizedHeap read (readStatics metadata) staticsOf term accessDefined (staticsOf state)
                result, withStatics state m'
            | Ptr(_, _, viewType, _) ->
                let ref = getReferenceFromPointer metadata term
                let term, state = hierarchicalAccess validate read actionNull updateDefined metadata state ref
                if typeOf term = viewType
                then term, state
                else __notImplemented__() // TODO: [columpio] [Reinterpretation]
            | t -> internalfailf "expected reference or pointer, but got %O" t
        guardedErroredStatedApply doAccess

// ------------------------------- Composition -------------------------------

    and private fillHole (ctx : compositionContext) state term =
        match term.term with
        | Constant(_, source, _) ->
            match source with
            | :? IStatedSymbolicConstantSource as source -> source.Compose ctx state
            | :? INonComposableSymbolicConstantSource -> term
            | _ -> __notImplemented__()
        | Concrete(:? concreteHeapAddress as addr', t) ->
            Concrete ctx.mtd (composeAddresses ctx.addr addr') t
        | Pointers.SymbolicThisOnStack(token, path) ->
            let id = ("this", token)
            let reference = referenceLocalVariable term.metadata state id false |> deref term.metadata state |> fst
            referenceSubLocations path reference
        | _ -> term

    and fillHoles ctx state term =
        Substitution.substitute (fillHole ctx state) (substituteTypeVariables ctx state) term

    and fillHolesInHeap fillHolesInKey ctx state heap =
        Heap.map (fun k cell -> (fillHolesInKey ctx state k, {cell with value = fillHoles ctx state cell.value})) heap

    and fillHolesInPathSegment ctx source = function
        | StructField(addr, typ) -> StructField(addr, substituteTypeVariables ctx source typ)
        | ArrayIndex(addr, typ) -> ArrayIndex(fillHoles ctx source addr, substituteTypeVariables ctx source typ)
        | ArrayLowerBound addr -> ArrayLowerBound(fillHoles ctx source addr)
        | ArrayLength addr -> ArrayLength(fillHoles ctx source addr)

    and private fillAndPushSegment ctx source path =
        Option.map (fillHolesInPathSegment ctx source)
     >> optCons path
     >> List.rev

    and private fillAndMutateStack (ctx : compositionContext) source (acc : foldSubLocationAccWrapper<_,stackKey,_>) _ segment cell = acc
//        let time = Timestamp.compose ctx.time cell.modified
//        let path' = fillAndPushSegment ctx source acc.path segment
//        let v = fillHoles ctx source cell.value
//        { acc with acc = mutateStack ctx.mtd acc.acc acc.loc path' time v }

    and private fillAndMutateCommon<'a when 'a : equality> mutateHeap (ctx : compositionContext) restricted source (acc : foldSubLocationAccWrapper<heap<'a,_,_>,'a,_>) typ segment cell =
        let time = Timestamp.compose ctx.time cell.modified
        let path' = fillAndPushSegment ctx source acc.path segment
        let v = fillHoles ctx source cell.value
        let typ = substituteTypeVariables ctx source typ |> specifyType
        { acc with acc = mutateHeap restricted ctx.mtd acc.acc acc.loc typ path' time v }

    and private composeDefinedHeaps writer fillHoles fillKey fillPath readHeap restricted s h h' =
        foldHeapLocations (writer restricted s) fillHoles fillKey fillPath (mergeDefinedHeaps false readHeap) h h'

    and private composeGeneralizedHeaps<'key when 'key : equality> writer fillHolesInKey readHeap (ctx : compositionContext) getter setter s (h' : 'key generalizedHeap) =
        match getter s, h' with
        | Defined(r, h), Defined(r', h') ->
            assert(not r')
            composeDefinedHeaps (writer ctx) (fillHoles ctx s) (fillHolesInKey ctx s) (fillHolesInPathSegment ctx s) (readHeap ctx.mtd) r s h h' |> Defined r
        | Merged ghs, _ ->
            let gs, hs = List.unzip ghs
            hs |> List.map (fun h -> composeGeneralizedHeaps writer fillHolesInKey readHeap ctx getter setter (setter s h) h') |> mergeGeneralizedHeaps (readHeap ctx.mtd) gs
        | _, Merged ghs' ->
            let gs, hs' = List.unzip ghs'
            let gs' = List.map (fillHoles ctx s) gs
            hs' |> List.map (composeGeneralizedHeaps writer fillHolesInKey readHeap ctx getter setter s) |> mergeGeneralizedHeaps (readHeap ctx.mtd) gs'
        | Defined _, Composition(s', ctx', h')
        | Mutation _, Composition(s', ctx', h')
        | Composition _, Composition(s', ctx', h') ->
            let s = composeStates ctx s s'
            composeGeneralizedHeaps writer fillHolesInKey readHeap ctx' getter setter s h'
        | Defined _, Mutation(h', h'')
        | RecursiveApplication _, Mutation(h', h'')
        | HigherOrderApplication _, Mutation(h', h'')
        | Composition _, Mutation(h', h'')
        | Mutation _, Mutation(h', h'') ->
            let res = composeGeneralizedHeaps writer fillHolesInKey readHeap ctx getter setter s h'
            let res' = fillHolesInHeap fillHolesInKey ctx s h''
            Mutation(res, res')
        | Defined _, HigherOrderApplication _
        | Defined _, RecursiveApplication _
        | Composition _, HigherOrderApplication _
        | Composition _, RecursiveApplication _
        | RecursiveApplication _, RecursiveApplication _
        | HigherOrderApplication _, HigherOrderApplication _ ->
            Composition(s, ctx, h')
        | Composition(s', ctx', h') as h, Defined(r'', h'') ->
            assert(not r'')
            match h' with
            | Defined(r, h') ->
                let ctx'' = decomposeContexts ctx ctx'
                let h = composeDefinedHeaps (writer ctx'') (fillHoles ctx s) (fillHolesInKey ctx'' s) (fillHolesInPathSegment ctx'' s) (readHeap ctx.mtd) r s h' h'' |> Defined r
                composeGeneralizedHeaps writer fillHolesInKey readHeap ctx' getter setter s' h
            | _ ->
                let h'' = fillHolesInHeap fillHolesInKey ctx s h''
                Mutation(h, h'')
        | (HigherOrderApplication _ as h), Defined(r, h')
        | (RecursiveApplication _ as h), Defined(r, h') ->
            assert(not r)
            let h' = fillHolesInHeap fillHolesInKey ctx s h'
            Mutation(h, h')
        | Mutation(h, h'), Defined(r, h'') ->
            // TODO: this is probably wrong!
            assert(not r)
            Mutation(h, composeDefinedHeaps (writer ctx) (fillHoles ctx s) (fillHolesInKey ctx s) (fillHolesInPathSegment ctx s) (readHeap ctx.mtd) false s h' h'')
        | RecursiveApplication _, Composition _ -> __notImplemented__()
        | HigherOrderApplication _, Composition _ -> __notImplemented__()
        | RecursiveApplication _, HigherOrderApplication _ -> __notImplemented__()
        | HigherOrderApplication _, RecursiveApplication _ -> __notImplemented__()
        | Mutation _, RecursiveApplication _ -> __notImplemented__()
        | Mutation _, HigherOrderApplication _ -> __notImplemented__()

    and composeStacksOf ctx state state' =
        (foldStackLocations (fillAndMutateStack ctx state) (fillHoles ctx state) (fillHolesInPathSegment ctx state) mergeStates state state'.stack).stack

    and composeHeapsOf ctx state heap =
        composeGeneralizedHeaps (fillAndMutateCommon mutateHeap) fillHoles readHeap ctx heapOf withHeap state heap

    and composeStaticsOf ctx state statics =
        composeGeneralizedHeaps (fillAndMutateCommon mutateStatics) substituteTypeVariables readStatics ctx staticsOf withStatics state statics

    and composeStates ctx state state' =
        let stack = composeStacksOf ctx state state'
        let heap = composeHeapsOf ctx state state'.heap
        let statics = composeStaticsOf ctx state state'.statics
        assert(state'.typeVariables |> snd |> Stack.isEmpty)
        let pc = List.map (fillHoles ctx state) state'.pc |> List.append state.pc
        { stack = stack; heap = heap; statics = statics; frames = state.frames; pc = pc; typeVariables = state.typeVariables }

// ------------------------------- High-level read/write -------------------------------

    and deref metadata state location =
        hierarchicalAccess true true npeTerm makePair metadata state location

    and derefWith actionNull metadata state location = hierarchicalAccess true true actionNull makePair metadata state location

    and derefWithoutValidation metadata state location =
        hierarchicalAccess false true (fun _ _ _ -> __unreachable__()) makePair metadata state location |> fst

    and mutate metadata state reference value =
        assert(value <> Nop)
        let time = tick()
        hierarchicalAccess true false npeTerm (fun _ _ -> (value, time)) metadata state reference

// ------------------------------- Referencing -------------------------------

    and private referenceTerm name followHeapRefs =
        guardedErroredApply (fun term ->
            match term.term with
            | Ref _ when followHeapRefs -> term
            | _ -> StackRef term.metadata name [])

    and referenceLocalVariable metadata state location followHeapRefs =
        let reference = StackRef metadata location []
        let term, _ = deref metadata state reference
        referenceTerm location followHeapRefs term

    let rec private referenceFieldOf state field parentRef reference =
        let referenceField state reference =
            match reference.term with
            | Ref(TopLevelHeap(addr, bT, sT), path) ->
                assert(List.isEmpty path)
                HeapRef reference.metadata addr bT sT [field], state
            | Null ->
                let term, state = npe reference.metadata state
                Error reference.metadata term, state
            | Struct _ -> referenceSubLocations [field] parentRef, state
            | t -> internalfailf "expected reference or struct, but got %O" t, state
        guardedErroredStatedApply referenceField state reference

    let rec private followOrReturnReference metadata state reference =
        let term, state = deref metadata state reference
        guardedErroredStatedApply (fun state term ->
            match term.term with
            | Ref _
            | Ptr _ -> term, state
            | _ -> reference, state) state term

    let referenceField metadata state followHeapRefs name typ parentRef =
        let typ = Types.wrapReferenceType typ
        let term, state = deref metadata state parentRef
        let reference, newState = referenceFieldOf state (StructField(name, typ)) parentRef term
        if followHeapRefs then followOrReturnReference metadata newState reference
        else (reference, newState)

    let referenceStaticField metadata state followHeapRefs fieldName fieldType targetType =
        let fieldType = Types.wrapReferenceType fieldType
        let reference = StaticRef metadata targetType [StructField(fieldName, fieldType)]
        if followHeapRefs then followOrReturnReference metadata state reference
        else (reference, state)

    let private checkIndices mtd state arrayRef (indices : term list) k =
        let intToTerm i = makeNumber mtd i
        let idOfDimensionsForLowerBounds = Seq.init indices.Length (intToTerm >> referenceArrayLowerBound arrayRef)
        let idOfDimensionsForLengths = Seq.init indices.Length (intToTerm >> referenceArrayLength arrayRef)
        Cps.Seq.mapFold (deref mtd) state idOfDimensionsForLowerBounds (fun (lowerBoundsList, state') ->
        Cps.Seq.mapFold (deref mtd) state' idOfDimensionsForLengths (fun (lengthsList, state'') ->
        let bounds =
            Seq.map3
                (fun idx low len ->
                    let up = add mtd low len
                    Arithmetics.simplifyGreaterOrEqual mtd idx low (fun bigEnough ->
                    Arithmetics.simplifyLess mtd idx up (fun smallEnough ->
                    bigEnough &&& smallEnough)))
                indices lowerBoundsList lengthsList
            |> List.ofSeq
        k (conjunction mtd bounds |> unguard |> merge , state'')))

    let referenceArrayIndex metadata state arrayRef (indices : term list) =
        let array, state = deref metadata state arrayRef
        // TODO: what about followHeapRefs?
        let rec reference = guardedErroredStatedApply (fun state term ->
            match term.term with
            | Array(_, _, _, _, _, _, ArrayType(elementType, _)) ->
                Common.statedConditionalExecution state
                    (fun state k -> checkIndices metadata state arrayRef indices k)
                    (fun state k ->
                        let location = Arrays.makeIndexArray metadata (fun i -> indices.[i]) indices.Length
                        let result = referenceSubLocations [ArrayIndex(location, elementType)] arrayRef
                        k (result, state))
                    (fun state k ->
                        let exn, state = State.createInstance metadata typeof<System.IndexOutOfRangeException> [] state
                        k (Error metadata exn, state))
                    merge merge2Terms id id
            | t -> internalfailf "accessing index of non-array term %O" t)
        reference state array

    let () =
        State.readHeap <- readHeap
        State.readStatics <- readStatics
        State.readTerm <- readTerm

// ------------------------------- Allocation -------------------------------

    let newStackFrame state metadata funcId frame = State.newStackFrame (tick()) metadata state funcId frame
    let newScope metadata state frame = State.newScope (tick()) metadata state frame

    let freshHeapLocation metadata =
        Concrete metadata ([freshAddress()]) pointerType

    let allocateOnStack metadata s key term =
        let time = tick()
        let { func = frameMetadata; entries = oldFrame; time = frameTime } = Stack.peek s.frames.f
        let newStack = pushToCurrentStackFrame s key { value = term; created = time; modified = time }
        let newEntries = { key = key; mtd = metadata; typ = typeOf term }
        let stackFrames = Stack.updateHead s.frames.f { func = frameMetadata; entries = newEntries :: oldFrame; time = frameTime }
        { s with stack = newStack; frames = { s.frames with f = stackFrames } }

    let private allocateInDefinedHeap (h : heap<'a, term, fql>) heapKey term time =
        Heap.add heapKey { value = term; created = time; modified = time } h

    let rec private allocateInGeneralizedHeap heapKey term time = function
        | Defined(r, h) -> allocateInDefinedHeap h heapKey term time |> Defined r
        | Composition _
        | RecursiveApplication _
        | HigherOrderApplication _ as h ->
            let mutatedHeap = allocateInDefinedHeap Heap.empty heapKey term time
            Mutation(h, mutatedHeap)
        | Mutation(gh, h) -> Mutation(gh, allocateInDefinedHeap h heapKey term time)
        | Merged gvh ->
            commonGuardedMapk (fun h k -> k <| allocateInGeneralizedHeap heapKey term time h) gvh
                (fun gvh ->
                    let g, h = List.unzip gvh
                    mergeGeneralizedHeaps (fun _ _ _ _ -> __unreachable__()) g h) id

    let allocateInHeap metadata s address term : term * state =
        let time = tick()
        let typ = typeOf term
        let ref = HeapRef metadata address typ typ []
        let heapKey = makeKey address <| makeTopLevelFQL TopLevelHeap (address, typ, typ)
        (ref, { s with heap = allocateInGeneralizedHeap heapKey term time s.heap } )

    let allocateString metadata state string =
        let address = freshHeapLocation metadata
        let fql = makeTopLevelFQL TopLevelHeap (address, String, String)
        Strings.makeConcreteStringStruct metadata (tick()) string fql |> allocateInHeap metadata state address

    let mkDefaultStaticStruct metadata state targetType fql =
        let dnt = toDotNetType targetType
        let time = tick()
        let defaultValue metadata _ typ fql' = defaultOf time metadata typ fql'
        let mkField, state =
            if targetType = String then
                let emptyStringRef, state = allocateString metadata state System.String.Empty
                let mkStringField metadata name typ fql' =
                    if name = "System.String.Empty" then emptyStringRef
                    else defaultValue metadata name typ fql'
                mkStringField, state
            else defaultValue, state
        mkStruct metadata time true mkField dnt targetType fql, state

    let allocateInStaticMemory _ (s : state) address term =
        let time = tick()
        let heapKey = makeTopLevelKey TopLevelStatics address
        { s with statics = allocateInGeneralizedHeap heapKey term time s.statics }

    let makeSymbolicThis metadata state token typ =
        let isRef = isReferenceType typ
        let thisKey = ((if isRef then "this" else Pointers.symbolicThisStackKey), token)
        let thisStackRef = StackRef metadata thisKey []
        let source = {location = thisStackRef; heap = None; extractor = IdTermExtractor(); typeExtractor = IdTypeExtractor()}
        let instance = makeSymbolicInstance metadata Timestamp.zero source source "this" None (wrapReferenceType typ)
        if isRef
            then instance, state, false
            else
                let state = newStackFrame state metadata (EmptyIdentifier()) [(thisKey, Specified instance, typ)]
                referenceLocalVariable metadata state thisKey true, state, true

// --------------------------------------- Is Location Initialized Check ---------------------------------------

    [<StructuralEquality;NoComparison>]
    type internal keyInitializedSource<'a when 'a : equality> =
        {heap : 'a generalizedHeap; key : 'a; getter : (state -> 'a generalizedHeap) transparent; fillHolesInKey : (compositionContext -> state -> 'a -> 'a) transparent }
        interface IStatedSymbolicConstantSource with
            override x.SubTerms = seq []

    let private mkKeyGuard mtd fillHolesInKey getter heap (key : 'a) =
        Constant mtd (IdGenerator.startingWith "hasKey#") ({ heap = heap; key = key; getter = {v=getter}; fillHolesInKey = {v=fillHolesInKey} } : 'a keyInitializedSource) Bool

    let private guardOfDefinedHeap mtd fillHolesInKey getter key r (h : heap<'key, term, fql>) =
        if Heap.contains key h then Merging.guardOf h.[key].value
        elif r then False
        else mkKeyGuard mtd fillHolesInKey getter (Defined r h) key

    let rec private guardOfHeap (exploredRecursiveIds : ImmutableHashSet<IFunctionIdentifier>) mtd fillHolesInKey getter key = function
        | Defined(r, h) -> guardOfDefinedHeap mtd fillHolesInKey getter key r h
        | Merged ghs -> guardedMap (guardOfHeap exploredRecursiveIds mtd fillHolesInKey getter key) ghs
        | Mutation(h, h') ->
            guardOfHeap exploredRecursiveIds mtd fillHolesInKey getter key h ||| guardOfDefinedHeap mtd fillHolesInKey getter key false h'
        | Composition(s, ctx, h) ->
            guardOfHeap exploredRecursiveIds mtd fillHolesInKey getter key (getter s) ||| guardOfHeap exploredRecursiveIds mtd fillHolesInKey getter (fillHolesInKey ctx s key) h
        | RecursiveApplication(f, _, _) when exploredRecursiveIds.Contains f -> False
        | RecursiveApplication(f, _, _) ->
            match Database.querySummary f with
            | Some summary ->
                guardOfHeap (exploredRecursiveIds.Add f) mtd fillHolesInKey getter key <| getter summary.state
            | None -> True
        | HigherOrderApplication _ as h ->
            mkKeyGuard mtd fillHolesInKey getter h key

    let private keyInitialized mtd key fillHolesInKey getter heap =
        guardOfHeap ImmutableHashSet<IFunctionIdentifier>.Empty mtd fillHolesInKey getter key heap

    let internal termTypeInitialized mtd termType state =
        keyInitialized mtd termType substituteTypeVariables staticsOf state.statics

    let internal termLocInitialized mtd loc state =
        keyInitialized mtd loc fillHoles heapOf state.heap

// ------------------------------- Compositions of constants -------------------------------

    type lazyInstantiation<'a when 'a : equality> with
        interface IExtractingSymbolicConstantSource with
            override x.Compose ctx state =
                let state' =
                    match x.heap with
                    | Some heap ->
                        // TODO: make it more effective (use lower-level functions to access heap directly instead of creating fresh state)
                        match x.location.term with // TODO: get rid of box someday
                        | Ref(TopLevelHeap _, _) -> { State.empty with heap = composeHeapsOf ctx state (box heap :?> term generalizedHeap) }
                        | Ref(TopLevelStatics _, _) -> { State.empty with statics = composeStaticsOf ctx state (box heap :?> termType generalizedHeap) }
                        | _ -> __notImplemented__()
                    | None -> state
                let loc = fillHoles ctx state x.location
                derefWithoutValidation ctx.mtd state' loc |> x.extractor.Extract

    type keyInitializedSource<'a when 'a : equality> with
        interface IStatedSymbolicConstantSource with
            override x.Compose ctx state =
                keyInitialized ctx.mtd x.key x.fillHolesInKey.v x.getter.v (x.getter.v state)
