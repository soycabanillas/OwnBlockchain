﻿namespace Own.Blockchain.Public.Core

open System
open Own.Common.FSharp
open Own.Blockchain.Common
open Own.Blockchain.Common.Conversion
open Own.Blockchain.Public.Core.Dtos
open Newtonsoft.Json
open Newtonsoft.Json.Converters
open Newtonsoft.Json.Linq
open MessagePack

module Serialization =

    let private tokenValue tokenName (jObject : JObject) =
        let token = ref (JValue("") :> JToken)
        let isValid = jObject.TryGetValue(tokenName, StringComparison.OrdinalIgnoreCase, token)
        if isValid then
            Some token.Value
        else
            None

    let private tokenToAction<'T> actionType (token : JToken option) =
        match token with
        | Some _ ->
            {
                ActionType = actionType
                ActionData =
                    match token with
                    | Some value -> value.ToObject<'T>()
                    | None -> failwith "ActionData is missing"
            }
            |> box
        | None ->
            token |> box

    let private actionTypeToObjectMapping =
        [
            "TransferChx", tokenToAction<TransferChxTxActionDto>
            "TransferAsset", tokenToAction<TransferAssetTxActionDto>
            "CreateAssetEmission", tokenToAction<CreateAssetEmissionTxActionDto>
            "CreateAccount", tokenToAction<CreateAccountTxActionDto>
            "CreateAsset", tokenToAction<CreateAssetTxActionDto>
            "SetAccountController", tokenToAction<SetAccountControllerTxActionDto>
            "SetAssetController", tokenToAction<SetAssetControllerTxActionDto>
            "SetAssetCode", tokenToAction<SetAssetCodeTxActionDto>
            "ConfigureValidator", tokenToAction<ConfigureValidatorTxActionDto>
            "RemoveValidator", tokenToAction<RemoveValidatorTxActionDto>
            "DelegateStake", tokenToAction<DelegateStakeTxActionDto>
            "SubmitVote", tokenToAction<SubmitVoteTxActionDto>
            "SubmitVoteWeight", tokenToAction<SubmitVoteWeightTxActionDto>
            "SetAccountEligibility", tokenToAction<SetAccountEligibilityTxActionDto>
            "SetAssetEligibility", tokenToAction<SetAssetEligibilityTxActionDto>
            "ChangeKycControllerAddress", tokenToAction<ChangeKycControllerAddressTxActionDto>
            "AddKycProvider", tokenToAction<AddKycProviderTxActionDto>
            "RemoveKycProvider", tokenToAction<RemoveKycProviderTxActionDto>
        ] |> Map.ofList

    let private actionsConverter = {
        new CustomCreationConverter<TxActionDto>() with

        override __.Create objectType =
            raise (NotImplementedException())

        override __.ReadJson
            (reader : JsonReader, objectType : Type, existingValue : obj, serializer : JsonSerializer)
            =

            let jObject = JObject.Load(reader)

            let actionData = tokenValue "ActionData"

            match (tokenValue "ActionType" jObject) with
            | None -> jObject |> box
            | Some actionType ->
                let txType = actionType.Value<string>()
                match txType |> actionTypeToObjectMapping.TryFind with
                | Some create ->
                    actionData jObject
                    |> create txType
                | None ->
                    {
                        ActionType = txType
                        ActionData =
                            match actionData jObject with
                            | None -> null
                            | Some x -> x.ToString()
                    } |> box
    }

    let serialize<'T> (dto : 'T) =
        try
            dto
            |> JsonConvert.SerializeObject
            |> stringToBytes
            |> Ok
        with
        | ex ->
            Result.appError ex.AllMessagesAndStackTraces

    let deserialize<'T> (rawData : byte[]) : Result<'T, AppErrors> =
        try
            rawData
            |> bytesToString
            |> fun str -> JsonConvert.DeserializeObject<'T>(str, actionsConverter)
            |> Ok
        with
        | ex ->
            Result.appError ex.AllMessagesAndStackTraces

    let serializeTx (txDto : TxDto) =
        serialize<TxDto> txDto

    let deserializeTx (rawTx : byte[]) : Result<TxDto, AppErrors> =
        deserialize<TxDto> rawTx

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Network
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let deserializeBinary<'T> (serializedMessageData : byte[]) =
        LZ4MessagePackSerializer.Deserialize<'T> serializedMessageData

    let private deserializeHandler<'T> = fun m -> m |> deserializeBinary<'T> |> box

    let private peerMessageTypeToObjectMapping =
        [
            "GossipDiscoveryMessage", deserializeHandler<GossipDiscoveryMessageDto>
            "GossipMessage", deserializeHandler<GossipMessageDto>
            "MulticastMessage", deserializeHandler<MulticastMessageDto>
            "RequestDataMessage", deserializeHandler<RequestDataMessageDto>
            "ResponseDataMessage", deserializeHandler<ResponseDataMessageDto>
        ]
        |> Map.ofList

    let serializeBinary dto =
        LZ4MessagePackSerializer.Serialize dto

    let deserializePeerMessage (message : byte[]) : Result<PeerMessageEnvelopeDto, AppErrors> =
        try
            let peerMessageEnvelopeDto = LZ4MessagePackSerializer.Deserialize<PeerMessageEnvelopeDto> message
            let deserialize = peerMessageTypeToObjectMapping.[peerMessageEnvelopeDto.PeerMessage.MessageType]
            { peerMessageEnvelopeDto with
                PeerMessage =
                    { peerMessageEnvelopeDto.PeerMessage with
                        MessageData = peerMessageEnvelopeDto.PeerMessage.MessageData :?> byte[] |> deserialize
                    }
            }
            |> Ok
        with
        | ex ->
            Result.appError ex.AllMessagesAndStackTraces
