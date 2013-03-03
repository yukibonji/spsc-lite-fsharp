﻿module ResidualProgramGenerator

open ExtCore
open ExtCore.Collections
open ExtCore.Control
open ExtCore.Control.Collections
open Algebra
open SLanguage
open ProcessTree

type Sig = Name * Name list
type Sigs = IntMap<Sig>


// isVarTest :: Tree -> Node -> Bool
let isVarTest (tree : Tree) { nodeChildren = bChId :: _ } : bool =
    (IntMap.find bChId tree).nodeContr
    |> Option.isSome

// getFGSig :: Tree -> String -> NodeId -> Name -> [Name] -> State (Sigs, [Rule]) Sig
let getFGSig (tree : Tree) (prefix : string) (nId : NodeId) (name : Name) (vs : Name list)
    : StateFunc<Sigs * Rule list, Sig> =
    state {
    let! sigs, rules = State.getState
    match IntMap.tryFind nId sigs with
    | None ->
        let name' = prefix + name.[1..] + (IntMap.count sigs + 1).ToString ()
        let sig' = (name', vs)
        let sigs' = IntMap.add nId sig' sigs
        do! State.setState (sigs', rules)
        return sig'

    | Some sig' ->
        return sig'
    }

// putFGRules :: [Rule] -> State (Sigs, [Rule]) ()
let putFGRules (newRules : Rule list) : StateFunc<Sigs * Rule list, unit> =
    state {
    let! sigs, rules = State.getState
    let rules' = rules @ newRules
    do! State.setState (sigs, rules')
    }

// getChContr :: Tree -> [NodeId] -> [(Name, [Name])]
let getChContr (tree : Tree) (nIds : NodeId list) : (Name * Name list) list =
    nIds
    |> List.choose (fun nId ->
        let child = IntMap.find nId tree
        match child.nodeContr with
        | Some (Contraction (_, cname, cparams)) ->
            Some (cname, cparams)
        | _ ->
            None)

// genResPrCall :: Tree -> Node -> Name -> [Exp] -> State (Sigs, [Rule]) Exp
let rec genResPrCall (tree : Tree) ({ nodeId = bId; nodeExp = bE; nodeChildren = bChIds } as b) (name : Name) (args : Exp list)
    : StateFunc<Sigs * Rule list, Exp> =
    state {
    let ``params`` = vars bE
    if isVarTest tree b then
        let! sigs, rules = State.getState
        let! name', _ = getFGSig tree "g" bId name ``params``
        let! bodies = genResPrExps tree bChIds
        let grules =
            let contrs = getChContr tree bChIds
            (contrs, bodies)
            ||> List.map2 (fun (cname', cparams') body' ->
                GRule (name', cname', cparams', (List.tail ``params``), body'))
        do! putFGRules grules
        return Call (GCall, name', List.map Var ``params``)

    elif isFuncNode tree bId then
        let! sigs, rules = State.getState
        let! name', params' = getFGSig tree "f" bId name ``params``
        let! body' = genResPrExp tree (IntMap.find (List.head bChIds) tree)
        do! putFGRules [FRule (name', params', body')]
        return Call (FCall, name', List.map Var ``params``)

    else
        return! genResPrExp tree (IntMap.find (List.head bChIds) tree)
    }

// genResPrExp :: Tree -> Node -> State (Sigs, [Rule]) Exp
and genResPrExp (tree : Tree) ({ nodeExp = bE; nodeChildren = bChIds } as b)
    : StateFunc<Sigs * Rule list, Exp> =
    state {
    match funcAncestors tree b with
    | [] ->
        match bE with
        | Var _ ->
            return bE
        | Call (Ctr, cname, _) ->
            let! es = genResPrExps tree bChIds
            return Call (Ctr, cname, es)
        | Call (FCall, name, args)
        | Call (GCall, name, args) ->        
            return! genResPrCall tree b name args
        | Let (_, bs) ->
            let! e' = genResPrExp tree (IntMap.find (List.head bChIds) tree)
            let! es' = genResPrExps tree (List.tail bChIds)
            let vnames = List.map fst bs
            let subst = Map.ofList (List.zip vnames es')
            return applySubst subst e'

    | { nodeId = aId; nodeExp = aE; nodeContr = aC; nodeChildren = aChId :: _ } :: _ ->
        let! sigs, rules = State.getState
        let name, ``params`` = IntMap.find aId sigs
        let args = List.map Var ``params``
        let subst =
            matchAgainst aE bE
            |> Option.get

        let callKind =
            match (IntMap.find aChId tree).nodeContr with
            | None -> FCall
            | Some _ -> GCall

        return applySubst subst <| Call (callKind, name, args)
    }

// genResPrExps :: Tree -> [NodeId] -> State (Sigs, [Rule]) [Exp]
and genResPrExps (tree : Tree) (nIds : NodeId list)
    : StateFunc<Sigs * Rule list, Exp list> =
    nIds
    |> List.map (fun nId ->
        IntMap.find nId tree)
    |> State.List.map (genResPrExp tree)

// genResidualProgram' :: Tree -> State (Sigs, [Rule]) (Program, Exp)
let genResidualProgram' (tree : Tree) : StateFunc<Sigs * Rule list, Program * Exp> =
    state {
    let! resExp = genResPrExp tree <| IntMap.find 0 tree
    let! (_, rules) = State.getState
    return (Program rules, resExp)
    }

// genResidualProgram :: Tree -> (Program, Exp)
let genResidualProgram (tree : Tree) : Program * Exp =
    (State.evaluate <| genResidualProgram' tree) (IntMap.empty, [])
