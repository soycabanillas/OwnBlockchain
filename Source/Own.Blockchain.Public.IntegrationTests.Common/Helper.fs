﻿namespace Own.Blockchain.Public.IntegrationTests.Common

open System
open System.IO
open System.Reflection
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Configuration
open Microsoft.Data.Sqlite
open Own.Blockchain.Public.Core.DomainTypes
open Own.Blockchain.Public.Crypto
open Own.Blockchain.Public.Data
open Own.Blockchain.Public.Node

module internal Helper =

    let private deleteFile filePath =
        let numberOfDeletionTrys = 20

        let rec tryDeleteFile filePath numOfChecks =
            if File.Exists filePath |> not || numOfChecks >= numberOfDeletionTrys then
                ()
            else
                try
                    File.Delete filePath
                with
                | :? System.IO.IOException as ex ->
                    if numOfChecks = numberOfDeletionTrys then
                        failwithf "%A" ex
                    else
                        System.Threading.Thread.Sleep(1000)
                        tryDeleteFile filePath numOfChecks

        tryDeleteFile filePath 0

    let testCleanup dbEngineType connString =
        if Directory.Exists(Config.DataDir) then
            Directory.Delete(Config.DataDir, true)

        if dbEngineType = Firebird then
            let conn = new SqliteConnection(connString)
            deleteFile conn.DataSource

        if dbEngineType = Postgres then
            let schemaName = (Npgsql.NpgsqlConnectionStringBuilder connString).SearchPath
            let removeAllTables =
                sprintf
                    """
                    DO $$ DECLARE
                        v_table_name TEXT;
                    BEGIN
                        FOR v_table_name IN
                            SELECT tablename
                            FROM pg_tables
                            WHERE schemaname = '%s'
                        LOOP
                            EXECUTE 'DROP TABLE IF EXISTS ' || QUOTE_IDENT(v_table_name) || ' CASCADE';
                        END LOOP;
                    END $$;
                    """
                    schemaName

            DbTools.execute dbEngineType connString removeAllTables [] |> ignore

    let testServer () =
        let hostBuilder =
            WebHostBuilder()
                .Configure(Action<IApplicationBuilder> Api.configureApp)
                .ConfigureServices(Api.configureServices)

        new TestServer(hostBuilder)

    let generateRandomHash () =
        Signing.generateRandomBytes 64
        |> Hashing.hash

    let addChxAddress dbEngineType connectionString (address : string) (amount : decimal) =
        let insertStatement =
            """
            INSERT INTO chx_address (blockchain_address, nonce, amount)
            VALUES (@blockchain_address, 0, @amount);
            """
        [
            "@blockchain_address", address |> box
            "@amount", amount |> box
        ]
        |> DbTools.execute dbEngineType connectionString insertStatement
        |> ignore

    let addChxAddressAndAccount dbEngineType connectionString (address : string) (balance : decimal) =
        let insertStatement =
            """
            INSERT INTO chx_address (blockchain_address, nonce, balance) VALUES (@blockchain_address, 0, @balance);
            INSERT INTO account (account_hash, controller_address) VALUES (@blockchain_address, @blockchain_address);
            """
        [
            "@blockchain_address", address |> box
            "@balance", balance |> box
        ]
        |> DbTools.execute dbEngineType connectionString insertStatement
        |> ignore

    let private appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    let private config =
        ConfigurationBuilder()
            .SetBasePath(appDir)
            .AddJsonFile("Config.json")
            .Build()

    let private genesis =
        ConfigurationBuilder()
            .SetBasePath(appDir)
            .AddJsonFile("Genesis.json")
            .Build()

    let BlockCreationWaitingTime =
        config.["BlockCreationWaitingTimeInSeconds"] |> int

    let ExpectedPathForFirstBlock = Path.Combine(Config.DataDir,"Block_1")
