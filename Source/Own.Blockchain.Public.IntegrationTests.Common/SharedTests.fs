﻿namespace Own.Blockchain.Public.IntegrationTests.Common

open System
open System.IO
open System.Threading
open System.Net.Http
open Own.Common.FSharp
open Own.Blockchain.Common
open Own.Blockchain.Public.Node
open Own.Blockchain.Public.Core.Dtos
open Own.Blockchain.Public.Crypto
open Own.Blockchain.Public.Data
open Own.Blockchain.Public.Core
open Own.Blockchain.Public.Core.DomainTypes
open Newtonsoft.Json
open MessagePack
open Swensen.Unquote

module SharedTests =

    let private addressFromPrivateKey = memoize Signing.addressFromPrivateKey

    let newTxDto (BlockchainAddress senderAddress) nonce actionFee actions =
        {
            SenderAddress = senderAddress
            Nonce = nonce
            ExpirationTime = 0L
            ActionFee = actionFee
            Actions = actions
        }

    let private transactionEnvelope sender txDto =
        let txBytes =
            txDto
            |> JsonConvert.SerializeObject
            |> Conversion.stringToBytes

        let (Signature signature) =
            txBytes
            |> Hashing.hash
            |> Signing.signHash (fun _ -> NetworkId Array.empty) sender.PrivateKey

        {
            Tx = System.Convert.ToBase64String(txBytes)
            Signature = signature
        }

    let private submitTransaction (client : HttpClient) txToSubmit =
        let tx = JsonConvert.SerializeObject(txToSubmit)
        let content = new StringContent(tx, System.Text.Encoding.UTF8, "application/json")

        let res =
            client.PostAsync("/tx", content)
            |> Async.AwaitTask
            |> Async.RunSynchronously

        let httpResult = res.EnsureSuccessStatusCode()

        httpResult.Content.ReadAsStringAsync()
        |> Async.AwaitTask
        |> Async.RunSynchronously
        |> JsonConvert.DeserializeObject<SubmitTxResponseDto>

    let private testInit dbEngineType connectionString =
        Helper.testCleanup dbEngineType connectionString
        DbInit.init dbEngineType connectionString
        Composition.initBlockchainState ()

        let testServer = Helper.testServer ()
        testServer.CreateClient()

    let submissionChecks
        dbEngineType
        connectionString
        shouldExist
        senderWallet
        (dto : TxDto)
        expectedTx
        (responseDto : SubmitTxResponseDto)
        =

        let fileName = sprintf "Tx_%s" responseDto.TxHash
        let txFile = Path.Combine(Config.DataDir, fileName)

        test <@ txFile |> File.Exists = shouldExist @>

        if shouldExist then
            let savedData =
                txFile
                |> File.ReadAllBytes
                |> LZ4MessagePackSerializer.Deserialize<TxEnvelopeDto>

            test <@ expectedTx = savedData @>

        let actual = Db.getTx dbEngineType connectionString (TxHash responseDto.TxHash)

        match actual with
        | None ->
            if shouldExist then
                failwith "Transaction is not stored into database"
        | Some txInfo ->
            test <@ responseDto.TxHash = txInfo.TxHash @>
            test <@ dto.ActionFee = txInfo.ActionFee @>
            test <@ dto.Nonce = txInfo.Nonce @>
            test <@ txInfo.SenderAddress = senderWallet.Address.Value @>

    let transactionSubmitTest dbEngineType connString isValidTransaction =
        let client = testInit dbEngineType connString
        let senderWallet = Signing.generateWallet ()
        let recipientWallet = Signing.generateWallet ()

        let action =
            {
                ActionType = "TransferChx"
                ActionData =
                    {
                        RecipientAddress = recipientWallet.Address.Value
                        TransferChxTxActionDto.Amount = 10m
                    }
            }

        let txDto = newTxDto senderWallet.Address 1L 1m [action]
        let expectedTx = transactionEnvelope senderWallet txDto

        submitTransaction client expectedTx
        |> submissionChecks dbEngineType connString isValidTransaction senderWallet txDto expectedTx

    let processTransactions expectedBlockPath =
        Workers.startFetcher ()

        let mutable iter = 0
        let sleepTime = 2

        while File.Exists(expectedBlockPath) |> not && iter < Helper.BlockCreationWaitingTime do
            Thread.Sleep(sleepTime * 1000)
            iter <- iter + sleepTime

        test <@ File.Exists(expectedBlockPath) @>

    let transactionProcessingTest dbEngineType connString =
        let client = testInit dbEngineType connString

        let submittedTxHashes =
            [
                for i in [1 .. 4] do
                    let senderWallet = Signing.generateWallet ()
                    let recipientWallet = Signing.generateWallet ()

                    Helper.addChxAddressAndAccount dbEngineType connString senderWallet.Address.Value 100m
                    Helper.addChxAddressAndAccount dbEngineType connString recipientWallet.Address.Value 0m

                    let isValid = i % 2 = 0
                    let amt = if isValid then 10m else -10m
                    // prepare transaction
                    let action =
                        {
                            ActionType = "TransferChx"
                            ActionData =
                                {
                                    RecipientAddress = recipientWallet.Address.Value
                                    TransferChxTxActionDto.Amount = amt
                                }
                        }

                    let actionFee = 1m
                    let nonce = 1L
                    let txDto = newTxDto senderWallet.Address nonce actionFee [action]

                    let expectedTx = transactionEnvelope senderWallet txDto

                    let submitedTransactionDto = submitTransaction client expectedTx
                    submissionChecks
                        dbEngineType
                        connString
                        isValid
                        senderWallet
                        txDto
                        expectedTx
                        submitedTransactionDto

                    if isValid then
                        yield submitedTransactionDto.TxHash
            ]

        test <@ submittedTxHashes.Length = 2 @>

        processTransactions Helper.ExpectedPathForFirstBlock

        for txHash in submittedTxHashes do
            let txResultFileName = sprintf "TxResult_%s" txHash
            let expectedTxResultPath = Path.Combine(Config.DataDir, txResultFileName)
            test <@ File.Exists expectedTxResultPath @>

            test <@ Db.getTx dbEngineType connString (TxHash txHash) = None @>

    let private numOfUpdatesExecuted dbEngineType connectionString =
        let sql =
            """
            SELECT version_number
            FROM db_version;
            """
        DbTools.query<int> dbEngineType connectionString sql []

    let initDatabaseTest dbEngineType connString =
        Helper.testCleanup dbEngineType connString
        DbInit.init dbEngineType connString
        let changes = numOfUpdatesExecuted dbEngineType connString
        test <@ changes.Length > 0 @>

    let initBlockchainStateTest dbEngineType connString =
        // ARRANGE
        Helper.testCleanup dbEngineType connString
        DbInit.init dbEngineType connString
        let expectedChxAddressState =
            {
                ChxAddressStateDto.Nonce = 0L
                Balance = Config.GenesisChxSupply
            }

        // ACT
        Composition.initBlockchainState ()

        // ASSERT
        let genesisAddressChxAddressState =
            Config.GenesisAddress
            |> BlockchainAddress
            |> Db.getChxAddressState dbEngineType connString

        let lastAppliedBlockNumber =
            Db.getLastAppliedBlockNumber dbEngineType connString

        test <@ genesisAddressChxAddressState = Some expectedChxAddressState @>
        test <@ lastAppliedBlockNumber = Some (BlockNumber 0L) @>

    let loadBlockTest dbEngineType connString =
        // ARRANGE
        Helper.testCleanup dbEngineType connString
        DbInit.init dbEngineType connString
        Composition.initBlockchainState ()

        let genesisValidators =
            Config.GenesisValidators
            |> List.map (fun (ba, na) ->
                BlockchainAddress ba,
                    (
                        {
                            ValidatorState.NetworkAddress = NetworkAddress na
                            SharedRewardPercent = 0m
                            TimeToLockDeposit = Config.ValidatorDepositLockTime |> Convert.ToInt16
                            TimeToBlacklist = 0s
                            IsEnabled = true
                        },
                        ValidatorChange.Add
                    )
            )
            |> Map.ofList

        let expectedBlockDto =
            Blocks.createGenesisState
                (ChxAmount Config.GenesisChxSupply)
                (BlockchainAddress Config.GenesisAddress)
                genesisValidators
            |> Blocks.assembleGenesisBlock
                Hashing.decode
                Hashing.hash
                Hashing.merkleTree
                Hashing.zeroHash
                Hashing.zeroAddress
                Config.ConfigurationBlockDelta
                (Convert.ToInt16 Config.ValidatorDepositLockTime)
                (Convert.ToInt16 Config.ValidatorBlacklistTime)
                Config.MaxTxCountPerBlock

        // ACT
        let loadedBlockDto =
            Raw.getBlock Config.DataDir (BlockNumber 0L)
            |> Result.map Blocks.extractBlockFromEnvelopeDto

        // ASSERT
        test <@ loadedBlockDto = Ok expectedBlockDto @>

    let getAccountStateTest dbEngineType connectionString =
        Helper.testCleanup dbEngineType connectionString
        DbInit.init dbEngineType connectionString
        let wallet = Signing.generateWallet ()

        let paramName = "@blockchain_address"

        let insertSql =
            String.Format
                (
                    """
                    INSERT INTO account (account_hash, controller_address)
                    VALUES ({0}, {0});
                    """,
                    paramName
                )

        let address = wallet.Address.Value

        [paramName, address |> box]
        |> Seq.ofList
        |> DbTools.execute dbEngineType connectionString insertSql
        |> ignore

        // TODO: Use separate account hash.
        match Db.getAccountState dbEngineType connectionString (AccountHash address) with
        | None -> failwith "Unable to get account state"
        | Some accountState -> test <@ BlockchainAddress accountState.ControllerAddress = wallet.Address @>

    let setAccountControllerTest dbEngineType connectionString =
        // ARRANGE
        let client = testInit dbEngineType connectionString

        let sender = Signing.generateWallet ()
        let newController = Signing.generateWallet ()
        let initialSenderChxBalance = 10m
        let initialValidatorChxBalance = 0m
        let (BlockchainAddress validatorAddress) =
            Config.ValidatorPrivateKey
            |> PrivateKey
            |> addressFromPrivateKey

        Helper.addChxAddressAndAccount dbEngineType connectionString sender.Address.Value initialSenderChxBalance
        Helper.addChxAddressAndAccount dbEngineType connectionString validatorAddress initialValidatorChxBalance

        let nonce = 1L
        let accountHash = Hashing.deriveHash sender.Address (Nonce nonce) (TxActionNumber 1s)

        let txActions =
            [
                {
                    ActionType = "CreateAccount"
                    ActionData = CreateAccountTxActionDto()
                }
                {
                    ActionType = "SetAccountController"
                    ActionData =
                        {
                            SetAccountControllerTxActionDto.AccountHash = accountHash
                            ControllerAddress = newController.Address.Value
                        }
                }
            ]

        let actionFee = 1m
        let totalFee = actionFee * (decimal txActions.Length)

        let txDto = newTxDto sender.Address nonce actionFee txActions
        let txEnvelope = transactionEnvelope sender txDto

        // ACT
        submitTransaction client txEnvelope
        |> submissionChecks dbEngineType connectionString true sender txDto txEnvelope

        processTransactions Helper.ExpectedPathForFirstBlock

        // ASSERT
        let accountState =
            Db.getAccountState dbEngineType connectionString (AccountHash accountHash)
            |> Option.map Mapping.accountStateFromDto

        let validatorAddress = Config.ValidatorPrivateKey |> PrivateKey |> addressFromPrivateKey
        let senderBalance = Db.getChxAddressState dbEngineType connectionString sender.Address
        let validatorBalance = Db.getChxAddressState dbEngineType connectionString validatorAddress

        test <@ accountState = Some { ControllerAddress = newController.Address } @>
        test <@ senderBalance = Some { Nonce = nonce; Balance = (initialSenderChxBalance - totalFee) } @>
        test <@ validatorBalance = Some { Nonce = 0L; Balance = (initialValidatorChxBalance + totalFee) } @>

    let setAssetControllerTest dbEngineType connectionString =
        // ARRANGE
        let client = testInit dbEngineType connectionString

        let sender = Signing.generateWallet ()
        let newController = Signing.generateWallet ()
        let initialSenderChxBalance = 10m
        let initialValidatorChxBalance = 0m
        let (BlockchainAddress validatorAddress) =
            Config.ValidatorPrivateKey
            |> PrivateKey
            |> addressFromPrivateKey

        Helper.addChxAddressAndAccount dbEngineType connectionString (sender.Address.Value) initialSenderChxBalance
        Helper.addChxAddressAndAccount dbEngineType connectionString validatorAddress initialValidatorChxBalance

        let nonce = 1L
        let assetHash = Hashing.deriveHash sender.Address (Nonce nonce) (TxActionNumber 1s)

        let txActions =
            [
                {
                    ActionType = "CreateAsset"
                    ActionData = CreateAssetTxActionDto()
                }
                {
                    ActionType = "SetAssetController"
                    ActionData =
                        {
                            SetAssetControllerTxActionDto.AssetHash = assetHash
                            ControllerAddress = newController.Address.Value
                        }
                }
            ]

        let actionFee = 1m
        let totalFee = actionFee * (decimal txActions.Length)

        let txDto = newTxDto sender.Address nonce actionFee txActions
        let txEnvelope = transactionEnvelope sender txDto

        // ACT
        submitTransaction client txEnvelope
        |> submissionChecks dbEngineType connectionString true sender txDto txEnvelope

        processTransactions Helper.ExpectedPathForFirstBlock

        // ASSERT
        let assetState =
            Db.getAssetState dbEngineType connectionString (AssetHash assetHash)
            |> Option.map Mapping.assetStateFromDto
        let senderBalance = Db.getChxAddressState dbEngineType connectionString sender.Address
        let validatorAddress = Config.ValidatorPrivateKey |> PrivateKey |> addressFromPrivateKey
        let validatorBalance = Db.getChxAddressState dbEngineType connectionString validatorAddress

        test <@ assetState =
            Some { AssetCode = None; ControllerAddress = newController.Address; IsEligibilityRequired = false } @>
        test <@ senderBalance = Some { Nonce = nonce; Balance = (initialSenderChxBalance - totalFee) } @>
        test <@ validatorBalance = Some { Nonce = 0L; Balance = (initialValidatorChxBalance + totalFee) } @>

    let setAssetCodeTest dbEngineType connectionString =
        // ARRANGE
        let client = testInit dbEngineType connectionString

        let assetCode = "Foo"
        let sender = Signing.generateWallet ()
        let initialSenderChxBalance = 10m
        let initialValidatorChxBalance = 0m
        let (BlockchainAddress validatorAddress) =
            Config.ValidatorPrivateKey
            |> PrivateKey
            |> addressFromPrivateKey

        Helper.addChxAddressAndAccount dbEngineType connectionString sender.Address.Value initialSenderChxBalance
        Helper.addChxAddressAndAccount dbEngineType connectionString validatorAddress initialValidatorChxBalance

        let nonce = 1L
        let assetHash = Hashing.deriveHash sender.Address (Nonce nonce) (TxActionNumber 1s)

        let txActions =
            [
                {
                    ActionType = "CreateAsset"
                    ActionData = CreateAssetTxActionDto()
                }
                {
                    ActionType = "SetAssetCode"
                    ActionData =
                        {
                            SetAssetCodeTxActionDto.AssetHash = assetHash
                            AssetCode = assetCode
                        }
                }
            ]

        let actionFee = 1m
        let totalFee = actionFee * (decimal txActions.Length)

        let txDto = newTxDto sender.Address nonce actionFee txActions
        let txEnvelope = transactionEnvelope sender txDto

        // ACT
        submitTransaction client txEnvelope
        |> submissionChecks dbEngineType connectionString true sender txDto txEnvelope

        processTransactions Helper.ExpectedPathForFirstBlock

        // ASSERT
        let assetCode = AssetCode assetCode
        let assetState =
            Db.getAssetState dbEngineType connectionString (AssetHash assetHash)
            |> Option.map Mapping.assetStateFromDto
        let senderBalance = Db.getChxAddressState dbEngineType connectionString sender.Address
        let validatorAddress = Config.ValidatorPrivateKey |> PrivateKey |> addressFromPrivateKey
        let validatorBalance = Db.getChxAddressState dbEngineType connectionString validatorAddress

        test <@ assetState =
            Some { AssetCode = Some assetCode; ControllerAddress = sender.Address; IsEligibilityRequired = false } @>
        test <@ senderBalance = Some { Nonce = nonce; Balance = (initialSenderChxBalance - totalFee) } @>
        test <@ validatorBalance = Some { Nonce = 0L; Balance = (initialValidatorChxBalance + totalFee) } @>

    let configureValidatorTest dbEngineType connectionString =
        // ARRANGE
        let client = testInit dbEngineType connectionString

        let expectedConfig =
            {
                NetworkAddress = NetworkAddress "localhost:5000"
                SharedRewardPercent = 42m
                TimeToLockDeposit = 0s
                TimeToBlacklist = 0s
                IsEnabled = true
            }
        let sender = Signing.generateWallet ()
        let initialSenderChxBalance = 10m
        let initialValidatorChxBalance = 0m
        let (BlockchainAddress validatorAddress) =
            Config.ValidatorPrivateKey
            |> PrivateKey
            |> addressFromPrivateKey

        Helper.addChxAddressAndAccount dbEngineType connectionString sender.Address.Value initialSenderChxBalance
        Helper.addChxAddressAndAccount dbEngineType connectionString validatorAddress initialValidatorChxBalance

        let nonce = 1L

        let txActions =
            [
                {
                    ActionType = "ConfigureValidator"
                    ActionData =
                        {
                            ConfigureValidatorTxActionDto.NetworkAddress = expectedConfig.NetworkAddress.Value
                            SharedRewardPercent = expectedConfig.SharedRewardPercent
                            IsEnabled = true
                        }
                }
            ]

        let actionFee = 1m
        let totalFee = actionFee * (decimal txActions.Length)

        let txDto = newTxDto sender.Address nonce actionFee txActions
        let txEnvelope = transactionEnvelope sender txDto

        // ACT
        submitTransaction client txEnvelope
        |> submissionChecks dbEngineType connectionString true sender txDto txEnvelope

        processTransactions Helper.ExpectedPathForFirstBlock

        // ASSERT
        let validatorState =
            Db.getValidatorState dbEngineType connectionString sender.Address
            |> Option.map Mapping.validatorStateFromDto
        let senderBalance = Db.getChxAddressState dbEngineType connectionString sender.Address
        let validatorAddress =
            Config.ValidatorPrivateKey
            |> PrivateKey
            |> addressFromPrivateKey

        let validatorBalance = Db.getChxAddressState dbEngineType connectionString validatorAddress

        test <@ validatorState = Some expectedConfig @>
        test <@ senderBalance = Some { Nonce = nonce; Balance = (initialSenderChxBalance - totalFee) } @>
        test <@ validatorBalance = Some { Nonce = 0L; Balance = (initialValidatorChxBalance + totalFee) } @>

    let delegateStakeTest dbEngineType connectionString =
        // ARRANGE
        let client = testInit dbEngineType connectionString

        let stakeValidatorAddress = (Signing.generateWallet ()).Address
        let stakeAmount = 5m
        let sender = Signing.generateWallet ()
        let initialSenderChxBalance = 10m
        let initialValidatorChxBalance = 0m

        Helper.addChxAddressAndAccount dbEngineType connectionString sender.Address.Value initialSenderChxBalance
        let (BlockchainAddress validatorAddress) = Config.ValidatorPrivateKey |> PrivateKey |> addressFromPrivateKey
        Helper.addChxAddressAndAccount dbEngineType connectionString validatorAddress initialValidatorChxBalance

        let nonce = 1L

        let txActions =
            [
                {
                    ActionType = "DelegateStake"
                    ActionData =
                        {
                            DelegateStakeTxActionDto.ValidatorAddress = stakeValidatorAddress.Value
                            Amount = stakeAmount
                        }
                }
            ]

        let actionFee = 1m
        let totalFee = actionFee * (decimal txActions.Length)

        let txDto = newTxDto sender.Address nonce actionFee txActions
        let txEnvelope = transactionEnvelope sender txDto

        // ACT
        submitTransaction client txEnvelope
        |> submissionChecks dbEngineType connectionString true sender txDto txEnvelope

        processTransactions Helper.ExpectedPathForFirstBlock

        // ASSERT
        let stakeState =
            Db.getStakeState dbEngineType connectionString (sender.Address, stakeValidatorAddress)
            |> Option.map Mapping.stakeStateFromDto
        let senderBalance = Db.getChxAddressState dbEngineType connectionString sender.Address
        let validatorAddress = Config.ValidatorPrivateKey |> PrivateKey |> addressFromPrivateKey
        let validatorBalance = Db.getChxAddressState dbEngineType connectionString validatorAddress

        test <@ stakeState = Some { StakeState.Amount = ChxAmount stakeAmount } @>
        test <@ senderBalance = Some { Nonce = nonce; Balance = (initialSenderChxBalance - totalFee) } @>
        test <@ validatorBalance = Some { Nonce = 0L; Balance = (initialValidatorChxBalance + totalFee) } @>
