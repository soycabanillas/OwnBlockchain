﻿namespace Own.Blockchain.Public.Core

open System
open Own.Common
open Own.Blockchain.Common
open Own.Blockchain.Public.Core.DomainTypes
open Own.Blockchain.Public.Core.Dtos

module Validation =

    let private validateDecimalCount dec count =
        let countDecimals dec =
            if dec = 0m then 0
            else
                let bits = Decimal.GetBits dec
                let exponent = bits.[3] >>> 16;
                let mutable result = exponent
                let mutable lowDecimal = bits.[0] ||| (bits.[1] >>> 8)
                while lowDecimal % 10 = 0 do
                    result <- result - 1
                    lowDecimal <- lowDecimal / 10
                result
        countDecimals dec <= count

    let validateDecimals dec = validateDecimalCount dec 7

    let validateTxEnvelope (txEnvelopeDto : TxEnvelopeDto) : Result<TxEnvelope, AppErrors> =
        [
            if txEnvelopeDto.Tx.IsNullOrWhiteSpace() then
                yield AppError "Tx is missing from the tx envelope."
            if txEnvelopeDto.Signature.IsNullOrWhiteSpace() then
                yield AppError "Signature is missing from the tx envelope."
        ]
        |> Errors.orElseWith (fun _ -> Mapping.txEnvelopeFromDto txEnvelopeDto)

    let validateBlockEnvelope (blockEnvelopeDto : BlockEnvelopeDto) : Result<BlockEnvelope, AppErrors> =
        [
            if blockEnvelopeDto.Block.IsNullOrWhiteSpace() then
                yield AppError "Block is missing from the block envelope."
            if blockEnvelopeDto.Signatures |> Array.isEmpty then
                yield AppError "Signatures are missing from the block envelope."
        ]
        |> Errors.orElseWith (fun _ -> Mapping.blockEnvelopeFromDto blockEnvelopeDto)

    let verifyTxSignature createHash verifySignature (txEnvelope : TxEnvelope) : Result<BlockchainAddress, AppErrors> =
        let txHash = createHash txEnvelope.RawTx
        match verifySignature txEnvelope.Signature txHash with
        | Some blockchainAddress ->
            Ok blockchainAddress
        | None ->
            Result.appError "Cannot verify tx signature."

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Block validation
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let validateBlock isValidAddress (blockDto : BlockDto) =
        [
            if blockDto.Header.Number < 0L then
                yield AppError "Block.Header.Number cannot be negative."

            if blockDto.Header.Hash.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.Hash is missing."

            if blockDto.Header.PreviousHash.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.PreviousHash is missing."

            if blockDto.Header.Timestamp < 0L then
                yield AppError "Block.Header.Timestamp cannot be negative."

            if blockDto.Header.ProposerAddress.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.ProposerAddress is missing."
            elif blockDto.Header.ProposerAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "Block.Header.ProposerAddress is not valid."

            if blockDto.Header.TxSetRoot.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.TxSetRoot is missing."

            if blockDto.Header.TxResultSetRoot.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.TxResultSetRoot is missing."

            if blockDto.Header.StateRoot.IsNullOrWhiteSpace() then
                yield AppError "Block.Header.StateRoot is missing."

            if blockDto.TxSet |> Seq.isEmpty then
                yield AppError "Block TxSet cannot be empty."
        ]
        |> Errors.orElseWith (fun _ -> Mapping.blockFromDto blockDto)

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // TxAction validation
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let private validateTransferChx isValidAddress (action : TransferChxTxActionDto) =
        [
            if action.RecipientAddress.IsNullOrWhiteSpace() then
                yield AppError "RecipientAddress is not provided."
            elif action.RecipientAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "RecipientAddress is not valid."

            if action.Amount <= 0m then
                yield AppError "CHX amount must be larger than zero."

            if not (validateDecimals action.Amount) then
                yield AppError "CHX amount must have at most 7 decimals."
        ]

    let private validateTransferAsset (action : TransferAssetTxActionDto) =
        [
            if action.FromAccountHash.IsNullOrWhiteSpace() then
                yield AppError "FromAccount value is not provided."

            if action.ToAccountHash.IsNullOrWhiteSpace() then
                yield AppError "ToAccount value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.Amount <= 0m then
                yield AppError "Asset amount must be larger than zero."

            if not (validateDecimals action.Amount) then
                yield AppError "Asset amount must have at most 7 decimals."
        ]

    let private validateCreateAssetEmission (action : CreateAssetEmissionTxActionDto) =
        [
            if action.EmissionAccountHash.IsNullOrWhiteSpace() then
                yield AppError "EmissionAccountHash value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.Amount <= 0m then
                yield AppError "Asset amount must be larger than zero."

            if not (validateDecimals action.Amount) then
                yield AppError "Asset amount must have at most 7 decimals."
        ]

    let private validateSetAccountController isValidAddress (action : SetAccountControllerTxActionDto) =
        [
            if action.AccountHash.IsNullOrWhiteSpace() then
                yield AppError "AccountHash is not provided."

            if action.ControllerAddress.IsNullOrWhiteSpace() then
                yield AppError "ControllerAddress is not provided."
            elif action.ControllerAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "ControllerAddress is not valid."
        ]

    let private validateSetAssetController isValidAddress (action : SetAssetControllerTxActionDto) =
        [
            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.ControllerAddress.IsNullOrWhiteSpace() then
                yield AppError "ControllerAddress is not provided."
            elif action.ControllerAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "ControllerAddress is not valid."
        ]

    let private validateSetAssetCode (action : SetAssetCodeTxActionDto) =
        [
            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.AssetCode.IsNullOrWhiteSpace() then
                yield AppError "AssetCode is not provided."
        ]

    let private validateConfigureValidator (action : ConfigureValidatorTxActionDto) =
        [
            if action.NetworkAddress.IsNullOrWhiteSpace() then
                yield AppError "NetworkAddress is not provided."

            if action.SharedRewardPercent < 0m then
                yield AppError "SharedRewardPercent cannot be negative."
            if action.SharedRewardPercent > 100m then
                yield AppError "SharedRewardPercent cannot be greater than 100."
        ]

    let private validateDelegateStake isValidAddress (action : DelegateStakeTxActionDto) =
        [
            if action.ValidatorAddress.IsNullOrWhiteSpace() then
                yield AppError "ValidatorAddress is not provided."
            elif action.ValidatorAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "ValidatorAddress is not valid."

            if action.Amount = 0m then
                yield AppError "CHX amount cannot be zero."

            if not (validateDecimals action.Amount) then
                yield AppError "CHX amount must have at most 7 decimals."
        ]

    let private validateSubmitVote (action : SubmitVoteTxActionDto) =
        [
            if action.AccountHash.IsNullOrWhiteSpace() then
                yield AppError "AccountHash value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.ResolutionHash.IsNullOrWhiteSpace() then
                yield AppError "ResolutionHash is not provided."

            if action.VoteHash.IsNullOrWhiteSpace() then
                yield AppError "VoteHash is not provided."
        ]

    let private validateSubmitVoteWeight (action : SubmitVoteWeightTxActionDto) =
        [
            if action.AccountHash.IsNullOrWhiteSpace() then
                yield AppError "AccountHash value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.ResolutionHash.IsNullOrWhiteSpace() then
                yield AppError "ResolutionHash is not provided."

            if action.VoteWeight < 0m then
                yield AppError "Vote weight cannot be negative."

            if not (validateDecimals action.VoteWeight) then
                yield AppError "Vote weight must have at most 7 decimals."
        ]

    let private validateSetEligibility (action : SetEligibilityTxActionDto) =
        [
            if action.AccountHash.IsNullOrWhiteSpace() then
                yield AppError "AccountHash value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."
        ]

    let private validateSetKycController
        isValidAddress
        (assetHash : string)
        (controllerAddress : string)
        =

        [
            if assetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if controllerAddress.IsNullOrWhiteSpace() then
                yield AppError "ValidatorAddress is not provided."
            elif controllerAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "ValidatorAddress is not valid."
        ]

    let private validateChangeKycControllerAddress isValidAddress (action : ChangeKycControllerAddressTxActionDto) =
        [
            if action.AccountHash.IsNullOrWhiteSpace() then
                yield AppError "AccountHash value is not provided."

            if action.AssetHash.IsNullOrWhiteSpace() then
                yield AppError "AssetHash is not provided."

            if action.KycControllerAddress.IsNullOrWhiteSpace() then
                yield AppError "ValidatorAddress is not provided."
            elif action.KycControllerAddress |> BlockchainAddress |> isValidAddress |> not then
                yield AppError "ValidatorAddress is not valid."
        ]

    let private validateAddKycController isValidAddress (action : AddKycControllerTxActionDto) =
        validateSetKycController isValidAddress action.AssetHash action.ControllerAddress

    let private validateRemoveKycController isValidAddress (action : RemoveKycControllerTxActionDto) =
        validateSetKycController isValidAddress action.AssetHash action.ControllerAddress

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Tx validation
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let private validateTxFields (BlockchainAddress signerAddress) (t : TxDto) =
        [
            if t.SenderAddress <> signerAddress then
                yield AppError "Sender address doesn't match the signature."
            if t.Nonce <= 0L then
                yield AppError "Nonce must be positive."
            if t.Fee <= 0m then
                yield AppError "Fee must be positive."
            if not (validateDecimals t.Fee) then
                yield AppError "Fee must be at most 7 decimal places."
            if t.Actions |> List.isEmpty then
                yield AppError "There are no actions provided for this transaction."
        ]

    let private validateTxActions isValidAddress (actions : TxActionDto list) =
        let validateTxAction (action : TxActionDto) =
            match action.ActionData with
            | :? TransferChxTxActionDto as a ->
                validateTransferChx isValidAddress a
            | :? TransferAssetTxActionDto as a ->
                validateTransferAsset a
            | :? CreateAssetEmissionTxActionDto as a ->
                validateCreateAssetEmission a
            | :? CreateAccountTxActionDto ->
                [] // Nothing to validate.
            | :? CreateAssetTxActionDto ->
                [] // Nothing to validate.
            | :? SetAccountControllerTxActionDto as a ->
                validateSetAccountController isValidAddress a
            | :? SetAssetControllerTxActionDto as a ->
                validateSetAssetController isValidAddress a
            | :? SetAssetCodeTxActionDto as a ->
                validateSetAssetCode a
            | :? ConfigureValidatorTxActionDto as a ->
                validateConfigureValidator a
            | :? DelegateStakeTxActionDto as a ->
                validateDelegateStake isValidAddress a
            | :? SubmitVoteTxActionDto as a ->
                validateSubmitVote a
            | :? SubmitVoteWeightTxActionDto as a ->
                validateSubmitVoteWeight a
            | :? SetEligibilityTxActionDto as a ->
                validateSetEligibility a
            | :? ChangeKycControllerAddressTxActionDto as a ->
                validateChangeKycControllerAddress isValidAddress a
            | :? AddKycControllerTxActionDto as a ->
                validateAddKycController isValidAddress a
            | :? RemoveKycControllerTxActionDto as a ->
                validateRemoveKycController isValidAddress a
            | _ ->
                let error = sprintf "Unknown action data type: %s" (action.ActionData.GetType()).FullName
                [AppError error]

        actions
        |> List.collect validateTxAction

    let validateTx isValidAddress sender hash (txDto : TxDto) : Result<Tx, AppErrors> =
        validateTxFields sender txDto
        @ validateTxActions isValidAddress txDto.Actions
        |> Errors.orElseWith (fun _ -> Mapping.txFromDto sender hash txDto)

    let checkIfBalanceCanCoverFees
        (getAvailableBalance : BlockchainAddress -> ChxAmount)
        getTotalFeeForPendingTxs
        senderAddress
        txFee
        : Result<unit, AppErrors>
        =

        let availableBalance = getAvailableBalance senderAddress

        if txFee > availableBalance then
            Result.appError "Available CHX balance is insufficient to cover the fee."
        else
            let totalFeeForPendingTxs = getTotalFeeForPendingTxs senderAddress

            if (totalFeeForPendingTxs + txFee) > availableBalance then
                Result.appError "Available CHX balance is insufficient to cover the fee for all pending transactions."
            else
                Ok ()

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // EquivocationProof validation
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let verifyEquivocationProofSignature
        (verifySignature : Signature -> string -> BlockchainAddress option)
        createConsensusMessageHash
        blockNumber
        consensusRound
        consensusStep
        (blockHash : string)
        signature
        =

        let consensusMessage =
            match consensusStep with
            | 0uy -> failwith "Equivocation is not checked on Propose messages."
            | 1uy -> blockHash |> Option.ofObj |> Option.map BlockHash |> ConsensusMessage.Vote
            | 2uy -> blockHash |> Option.ofObj |> Option.map BlockHash |> ConsensusMessage.Commit
            | c -> failwithf "Unknown consensus step code: %i" c

        let consensusMessageHash =
            createConsensusMessageHash
                (blockNumber |> BlockNumber)
                (consensusRound |> ConsensusRound)
                consensusMessage

        verifySignature (Signature signature) consensusMessageHash

    let validateEquivocationProof
        (verifySignature : Signature -> string -> BlockchainAddress option)
        createConsensusMessageHash
        decodeHash
        createHash
        (equivocationProofDto : EquivocationProofDto)
        : Result<EquivocationProof, AppErrors>
        =

        let signer1 =
            verifyEquivocationProofSignature
                verifySignature
                createConsensusMessageHash
                equivocationProofDto.BlockNumber
                equivocationProofDto.ConsensusRound
                equivocationProofDto.ConsensusStep
                equivocationProofDto.BlockHash1
                equivocationProofDto.Signature1
        let signer2 =
            verifyEquivocationProofSignature
                verifySignature
                createConsensusMessageHash
                equivocationProofDto.BlockNumber
                equivocationProofDto.ConsensusRound
                equivocationProofDto.ConsensusStep
                equivocationProofDto.BlockHash2
                equivocationProofDto.Signature2

        match signer1, signer2 with
        | None, _ ->
            Result.appError "Cannot verify signature 1."
        | _, None ->
            Result.appError "Cannot verify signature 2."
        | Some s1, Some s2 ->
            if s1 <> s2 then
                sprintf "Signatures are not from the same address (%s / %s)" s1.Value s2.Value
                |> Result.appError
            else
                let validatorAddress = s1
                let equivocationProofHash =
                    [
                        equivocationProofDto.BlockNumber |> Conversion.int64ToBytes
                        equivocationProofDto.ConsensusRound |> Conversion.int32ToBytes
                        [| equivocationProofDto.ConsensusStep |]
                        equivocationProofDto.BlockHash1 |> Option.ofObj |> Option.map decodeHash |? Array.empty
                        equivocationProofDto.BlockHash2 |> Option.ofObj |> Option.map decodeHash |? Array.empty
                        equivocationProofDto.Signature1 |> decodeHash
                        equivocationProofDto.Signature2 |> decodeHash
                    ]
                    |> Array.concat
                    |> createHash
                    |> EquivocationProofHash

                {
                    EquivocationProofHash = equivocationProofHash
                    ValidatorAddress = validatorAddress
                    BlockNumber = equivocationProofDto.BlockNumber |> BlockNumber
                    ConsensusRound = equivocationProofDto.ConsensusRound |> ConsensusRound
                    ConsensusStep = equivocationProofDto.ConsensusStep |> Mapping.consensusStepFromCode
                    BlockHash1 = equivocationProofDto.BlockHash1 |> Option.ofObj |> Option.map BlockHash
                    BlockHash2 = equivocationProofDto.BlockHash2 |> Option.ofObj |> Option.map BlockHash
                    Signature1 = equivocationProofDto.Signature1 |> Signature
                    Signature2 = equivocationProofDto.Signature2 |> Signature
                }
                |> Ok
