﻿namespace Own.Blockchain.Public.Net

open System
open System.Collections.Concurrent
open Own.Common
open Own.Blockchain.Common
open Own.Blockchain.Public.Core
open Own.Blockchain.Public.Core.Dtos
open NetMQ
open NetMQ.Sockets
open Newtonsoft.Json

module Transport =

    let private poller = new NetMQPoller()
    let mutable private routerSocket : RouterSocket option = None
    let private dealerSockets = new ConcurrentDictionary<string, DealerSocket>()
    let private dealerMessageQueue = new NetMQQueue<string * NetMQMessage>()
    let private routerMessageQueue = new NetMQQueue<NetMQMessage>()
    let mutable peerMessageHandler : (PeerMessageDto -> unit) option = None
    let mutable identity : byte[] option = None

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Private
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let private composeMultipartMessage (msg : byte[]) (identity : byte[] option) =
        let multipartMessage = new NetMQMessage();
        identity |> Option.iter(fun id -> multipartMessage.Append(id))
        multipartMessage.AppendEmptyFrame();
        multipartMessage.Append(msg);
        multipartMessage

    let private extractMessageFromMultipart (multipartMessage : NetMQMessage) =
        if multipartMessage.FrameCount = 3 then
            let originatorIdentity = multipartMessage.[0]; // TODO: use this instead of SenderIdentity
            let msg = multipartMessage.[2].ToByteArray();
            msg |> Some
        else
            Log.errorf "Invalid message frame count. Expected 3, received %i" multipartMessage.FrameCount
            None

    let private packMessage message (identity : byte[] option) =
        let bytes = message |> Serialization.serializeBinary
        composeMultipartMessage bytes identity

    let private unpackMessage message =
        message |> Serialization.deserializePeerMessage

    let private receiveMessageCallback (eventArgs : NetMQSocketEventArgs) =
        let mutable message = new NetMQMessage()
        if eventArgs.Socket.TryReceiveMultipartMessage &message then
            extractMessageFromMultipart message
            |> Option.iter(fun m ->
                match unpackMessage m with
                | Ok peerMessage ->
                    peerMessageHandler |> Option.iter(fun handler -> handler peerMessage)
                | Error error -> Log.error error
            )

    let private createDealerSocket targetHost =
        let dealerSocket = new DealerSocket("tcp://" + targetHost)
        identity |> Option.iter(fun id -> dealerSocket.Options.Identity <- id)
        dealerSocket.ReceiveReady
        |> Observable.subscribe (fun e ->
            let hasMore, _ = e.Socket.TryReceiveFrameString()
            if hasMore then
                let mutable message = Array.empty<byte>
                if e.Socket.TryReceiveFrameBytes &message then
                    match unpackMessage message with
                    | Ok peerMessage ->
                        peerMessageHandler |> Option.iter(fun handler -> handler peerMessage)
                    | Error error -> Log.error error
        )
        |> ignore

        dealerSocket

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Dealer
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let private dealerSendAsync (msg : NetMQMessage) targetAddress =
        let found, _ = dealerSockets.TryGetValue targetAddress
        if not found then
            let dealerSocket = createDealerSocket targetAddress
            poller.Add dealerSocket
            dealerSockets.AddOrUpdate (targetAddress, dealerSocket, fun _ _ -> dealerSocket) |> ignore
        dealerMessageQueue.Enqueue (targetAddress, msg)

    dealerMessageQueue.ReceiveReady |> Observable.subscribe (fun e ->
        let mutable message = "", new NetMQMessage()
        while e.Queue.TryDequeue(&message, TimeSpan.FromMilliseconds(10.)) do
            let targetAddress, payload = message
            match dealerSockets.TryGetValue targetAddress with
            | true, socket -> socket.TrySendMultipartMessage payload |> ignore
            | _ -> failwithf "Socket not found for target %s" targetAddress
    )
    |> ignore

    poller.Add dealerMessageQueue

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Router
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let private routerSendAsync (msg : NetMQMessage) =
        routerMessageQueue.Enqueue msg

    routerMessageQueue.ReceiveReady |> Observable.subscribe (fun e ->
        let mutable message = new NetMQMessage()
        while e.Queue.TryDequeue(&message, TimeSpan.FromMilliseconds(10.)) do
            routerSocket |> Option.iter(fun socket -> socket.TrySendMultipartMessage message |> ignore)
    )
    |> ignore

    poller.Add routerMessageQueue

    let receiveMessage peerIdentity listeningAddress receivePeerMessage =
        match identity with
        | Some _ -> ()
        | None -> identity <- peerIdentity |> Some

        match routerSocket with
        | Some _ -> ()
        | None -> routerSocket <- new RouterSocket("@tcp://" + listeningAddress) |> Some

        match peerMessageHandler with
        | Some _ -> ()
        | None -> peerMessageHandler <- receivePeerMessage |> Some

        routerSocket |> Option.iter(fun socket ->
            poller.Add socket
            socket.ReceiveReady
            |> Observable.subscribe receiveMessageCallback
            |> ignore
        )
        if not poller.IsRunning then
            poller.RunAsync()

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Send
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let sendGossipDiscoveryMessage gossipDiscoveryMessage targetAddress =
        let msg = packMessage gossipDiscoveryMessage None
        dealerSendAsync msg targetAddress

    let sendGossipMessage gossipMessage (targetMember: GossipMemberDto) =
        let msg = packMessage gossipMessage None
        dealerSendAsync msg targetMember.NetworkAddress

    let sendRequestMessage requestMessage targetAddress =
        let msg = packMessage requestMessage None
        dealerSendAsync msg targetAddress

    let sendResponseMessage responseMessage (targetIdentity : byte[]) =
        let msg = packMessage responseMessage (targetIdentity |> Some)
        routerSendAsync msg

    let sendMulticastMessage multicastMessage multicastAddresses =
        match multicastAddresses with
        | [] -> ()
        | _ ->
            multicastAddresses
            |> Seq.shuffle
            |> Seq.iter (fun networkAddress ->
                let msg = packMessage multicastMessage None
                dealerSendAsync msg networkAddress
            )

    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Cleanup
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let closeConnection remoteAddress =
        match dealerSockets.TryGetValue remoteAddress with
        | true, socket ->
            dealerSockets.TryRemove remoteAddress |> ignore
            socket.Dispose()
        | _ -> ()

    let closeAllConnections () =
        if poller.IsRunning then
            poller.Dispose()
            dealerMessageQueue.Dispose()

        dealerSockets
        |> List.ofDict
        |> List.iter (fun (_, socket) ->
            socket.Dispose()
        )
        dealerSockets.Clear()

        routerSocket |> Option.iter (fun socket -> socket.Dispose())
        routerSocket <- None
