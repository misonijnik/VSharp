﻿namespace VSharp.Core

open VSharp
open Arrays
open Types

module internal Strings =

    let strLength = "System::String::m_StringLength"
    let strArray = "System::String::m_FirstChar"

    let stringArrayType = ArrayType(Char, Vector)

    let makeArrayFQL fql = addToOptionFQL fql <| BlockField(FieldId strArray, stringArrayType)
    let makeLengthFQL fql = addToOptionFQL fql <| BlockField(FieldId strLength, lengthType)

    let makeArrayFieldKey fql = makeKey strArray (makeArrayFQL fql) stringArrayType
    let makeLengthFieldKey fql = makeKey strLength (makeLengthFQL fql) lengthType

    let makeStringOfFields metadata length array arrayFQL fql =
        let fields = Heap.ofSeq (seq [ makeLengthFieldKey fql, length;
                                       makeKey strArray arrayFQL stringArrayType, array])
        Class metadata fields

    let makeConcreteStringStruct metadata (str : string) fql =
        let length = Concrete metadata str.Length lengthType
        let arraySource = (str + "\000").ToCharArray()
        let valMaker i = makeNumber metadata arraySource.[i]
        let keyMaker mtd i = makeIndexArray metadata (fun _ -> makeIndex mtd i) 1
        let arrayFQL = makeArrayFQL fql
        let array = makeLinearConcreteArray metadata keyMaker valMaker (str.Length + 1) Char arrayFQL
        makeStringOfFields metadata length array arrayFQL fql

    let makeStringArray metadata length instor contents arrayFQL =
        let arrLength = makeNumber metadata 1 |> add metadata length
        let indexLength = makeIndexArray metadata (always length) 1
        let indexLengthKey = makePathKey arrayFQL (mkArrayIndex Char) indexLength Char
        let contentsWithZero = Heap.add indexLengthKey (makeNumber metadata '\000') contents
        makeArray metadata arrLength contentsWithZero instor arrayFQL

    let ctorOfCharArray metadata fql = Merging.guardedErroredApply (function
        | VectorT(length, instor, contents) ->
            let arrayFQL = makeArrayFQL fql
            let stringArray = makeStringArray metadata length instor contents arrayFQL
            makeStringOfFields metadata length stringArray arrayFQL fql
        | t -> internalfailf "expected char array, but got %O" t)

    let length fql = Merging.guardedErroredApply (term >> function
        | Class fields -> fields.[makeLengthFieldKey fql]
        | t -> internalfailf "expected string struct, but got %O" t)

    let simplifyStructEq mtd x xFQL y yFQL =
        match x.term, y.term with
        | Class fieldsOfX, Class fieldsOfY ->
            let str1Len = fieldsOfX.[makeLengthFieldKey xFQL]
            let str2Len = fieldsOfY.[makeLengthFieldKey yFQL]
            let str1Arr = fieldsOfX.[makeArrayFieldKey xFQL]
            let str2Arr = fieldsOfY.[makeArrayFieldKey yFQL]
            simplifyEqual mtd str1Len str2Len (fun lengthEq ->
            simplifyAnd mtd lengthEq (Arrays.equals mtd str1Arr str2Arr) id)
        | _ -> internalfailf "expected string struct and string struct, but got %O and %O" x y
