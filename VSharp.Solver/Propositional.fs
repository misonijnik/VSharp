namespace VSharp.Solver

open VSharp
open VSharp.Core

module internal Propositional =
    let True = Concrete (box true) Bool
    let False = Concrete (box false) Bool

    let isBoolType = function
        | Bool -> true
        | _ -> false

    let isBool term = API.Terms.TypeOf term |> isBoolType

    let isTrue term = term.term |> function
        | Concrete(b, t) when isBoolType t && (b :?> bool) -> true
        | _ -> false

    let isFalse term = term.term |> function
        | Concrete(b, t) when isBoolType t && not (b :?> bool) -> true
        | _ -> false

    let (|True|_|) term = if isTrue term then Some True else None
    let (|False|_|) term = if isFalse term then Some False else None
    let (|BoolConstant|_|) term = if isBool term then Some BoolConstant else None
    let (|Negation|_|) term = term.term |> function
        | Expression(Operator(OperationType.LogicalNeg, _), [x], _) -> Some(Negation x)
        | _ -> None

    let rec notProp x =
        match x with
        | True -> False
        | False -> True
        | Negation x -> x
        | Conjunction xs -> disjunction (List.map notProp xs)
        | Disjunction xs -> conjunction (List.map notProp xs)
        | _ -> Expression (Operator(OperationType.LogicalNeg, false)) [x] Bool

    and andProp x y =
        match x, y with
        | False, _
        | _, False -> False
        | True, y -> y
        | x, True -> x
        | _ -> Expression (Operator(OperationType.LogicalAnd, false)) [x; y] Bool

    and orProp x y =        
        match x, y with
        | True, _
        | _, True -> True
        | False, y -> y
        | x, False -> x
        | _ -> Expression (Operator(OperationType.LogicalOr, false)) [x; y] Bool

    and conjunction = function
        | Seq.Cons(x, xs) ->
            if Seq.isEmpty xs then x
            else Seq.fold andProp x xs
        | _ -> True
    and disjunction = function
        | Seq.Cons(x, xs) ->
            if Seq.isEmpty xs then x
            else Seq.fold orProp x xs
        | _ -> False

    let private z3Simplifier = Z3Simplifier() :> IPropositionalSimplifier
    
    let rec private simplify alpha x k =
        let criticalConstraintFolder star (rlist, llist, flag) c k =
            let nllist = conjunction (List.map star llist)
            let nrlist = conjunction (List.map star rlist)
            let nalpha = conjunction [alpha; nllist; nrlist]
            simplify nalpha c (fun nc ->
            let rlist = if rlist.IsEmpty then rlist else List.tail rlist 
            k (rlist, nc :: llist, flag && c = nc))

        let rec criticalConstraint star connective xs k =
            Cps.List.foldlk (criticalConstraintFolder star) (List.tail xs, [], true) xs (fun (_, reslist, flag) ->
                if flag
                    then k <| connective (List.rev reslist)
                    else criticalConstraint star connective (List.rev reslist) k)

        match x with
        | API.Terms.Error _ -> k x
        | Conjunction xs -> criticalConstraint id conjunction xs k
        | Disjunction xs -> criticalConstraint notProp disjunction xs k        
        | GuardedValues(gs, vs) ->
            Cps.List.mapk (simplify alpha) vs (List.zip gs >> Union >> k)
        | BoolConstant
        | Negation _ ->
            let notAlpha = notProp alpha
            let first = orProp notAlpha x
            let simpleFirst = z3Simplifier.Simplify first
            k <| if isTrue simpleFirst
                then True
                else
                    let notX = notProp x
                    let second = orProp notAlpha notX
                    let simpleSecond = z3Simplifier.Simplify second
                    if isTrue simpleSecond then False else x
        | _ -> internalfail "Всё плохо :("

    let public simplifyPropositional x = simplify True x id

type public ScalableSimplifier() =
    interface IPropositionalSimplifier with
        member x.Simplify t = Propositional.simplifyPropositional t