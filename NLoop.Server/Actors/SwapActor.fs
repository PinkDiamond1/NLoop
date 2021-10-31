namespace NLoop.Server.Actors

open System
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g payInvoice =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.GetRefundAddress = g
      Swap.Deps.PayInvoice = payInvoice
      }

type SwapActor(broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetAddress,
               lightningClientProvider: ILightningClientProvider,
               invoiceProvider: ILightningInvoiceProvider,
               opts: IOptions<NLoopOptions>,
               logger: ILogger<SwapActor>,
               eventAggregator: IEventAggregator,
               boltzClient: BoltzClient
  )  =

  let aggr =
    let payInvoice =
      fun (n: Network) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let cc = n.ChainName.ToString() |> SupportedCryptoCode.TryParse
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee
          OutgoingChannelIds = param.OutgoingChannelIds
        }
        task {
          match! lightningClientProvider.GetClient(cc.Value).Offer(req) with
          | Ok r ->
            return {
              Swap.PayInvoiceResult.RoutingFee = r.Fee
              Swap.PayInvoiceResult.AmountPayed = i.AmountValue.Value
            }
          | Error e -> return raise <| exn $"Failed payment {e}"
        }
    getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress payInvoice
    |> Swap.getAggregate
  let handler =
    Swap.getHandler aggr (opts.Value.EventStoreUrl |> Uri)

  member val Handler = handler with get
  member val Aggregate = aggr with get

  member this.Execute(swapId, msg: Swap.Command, ?source) = task {
    logger.LogDebug($"New Command {msg}")
    let source = source |> Option.defaultValue (nameof(SwapActor))
    let cmd =
      { ESCommand.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = UnixDateTime.UtcNow } }
    match! handler.Execute swapId cmd with
    | Ok events ->
      events
      |> List.iter(fun e ->
        eventAggregator.Publish e
        eventAggregator.Publish e.Data
        eventAggregator.Publish({ Swap.EventWithId.Id = swapId; Swap.EventWithId.Event = e.Data })
      )
    | Error (EventSourcingError.Store s as e) ->
      logger.LogError($"Store Error when executing swap handler %A{s}")
      eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = e })
      // todo: retry
      ()
    | Error s ->
      logger.LogError($"Error when executing swap handler %A{s}")
      eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = s })
  }

  /// Helper function for creating new loop out.
  /// Technically, the logic of this function should be in the Domain layer, but we want
  /// swapId to be the StreamId of the event stream, we must
  /// get the StreamId outside of the Domain, So we must call `BoltzClient.CreateReverseSwap` and get the swapId before
  /// sending the command into the domain layer.
  /// If we define some internal UUID for swapid instead of using the one given by the boltz server, the logic of this
  /// function can go into the domain layer. But that complicates things by having two kinds of IDs for each swaps.
  member this.ExecNewLoopOut(
                             req: LoopOutRequest,
                             height: BlockHeight
                             ) = taskResult {
      let claimKey = new Key()
      let preimage = RandomUtils.GetBytes 32 |> PaymentPreimage.Create
      let preimageHash = preimage.Hash
      let struct(baseCryptoCode, quoteCryptoCode) as pairId =
        req.PairId
        |> Option.defaultValue PairId.Default

      let n = opts.Value.GetNetwork(baseCryptoCode)
      let! outResponse =
        let req =
          { CreateReverseSwapRequest.InvoiceAmount = req.Amount
            PairId = pairId
            OrderSide = OrderType.buy
            ClaimPublicKey = claimKey.PubKey
            PreimageHash = preimageHash.Value }
        boltzClient.CreateReverseSwapAsync req
      let lnClient = lightningClientProvider.GetClient(quoteCryptoCode)

      let! addr =
        match req.Address with
        | Some a -> Task.FromResult a
        | None ->
          lnClient.GetDepositAddress()
      let loopOut = {
        LoopOut.Id = outResponse.Id |> SwapId
        LoopOut.Status = SwapStatusType.SwapCreated
        LoopOut.ClaimKey = claimKey
        Preimage = preimage
        RedeemScript = outResponse.RedeemScript
        Invoice = outResponse.Invoice.ToString()
        ClaimAddress = addr.ToString()
        OnChainAmount = outResponse.OnchainAmount
        TimeoutBlockHeight = outResponse.TimeoutBlockHeight
        LockupTransactionHex = None
        ClaimTransactionId = None
        PairId = pairId
        ChainName = opts.Value.ChainName.ToString()
        Label = req.Label |> Option.defaultValue String.Empty
        PrepayInvoice =
          outResponse.MinerFeeInvoice
          |> Option.map(fun s -> s.ToString())
          |> Option.defaultValue String.Empty
        MaxMinerFee = req.Limits.MaxMinerFee
        SweepConfTarget =
          req.SweepConfTarget
          |> ValueOption.defaultValue Constants.DefaultSweepConfTarget
          |> uint |> BlockHeightOffset32
        IsClaimTxConfirmed = false
        IsOffchainOfferResolved = false
        Cost = SwapCost.Zero
        LoopOut.SwapTxConfRequirement =
          req.Limits.HTLCConfTarget
        LockupTransactionHeight = None
      }
      match outResponse.Validate(preimageHash.Value,
                                 claimKey.PubKey,
                                 req.Amount,
                                 req.MaxSwapFee |> ValueOption.defaultToVeryHighFee,
                                 req.MaxPrepayAmount |> ValueOption.defaultToVeryHighFee,
                                 n) with
      | Error e ->
        return! Error e
      | Ok () ->
        let loopOutParams = {
          Swap.LoopOutParams.OutgoingChanIds = req.ChannelId
          Swap.LoopOutParams.MaxPrepayFee = req.MaxPrepayRoutingFee |> ValueOption.defaultValue(Money.Coins 100000m)
          Swap.LoopOutParams.MaxPaymentFee = req.MaxSwapFee |> ValueOption.defaultValue(Money.Coins 100000m)
          Swap.LoopOutParams.Height = height
        }
        do! this.Execute(loopOut.Id, Swap.Command.NewLoopOut(loopOutParams, loopOut))
        return loopOut
  }


  member this.ExecNewLoopIn(loopIn: LoopInRequest, height: BlockHeight) = taskResult {
      let pairId =
        loopIn.PairId
        |> Option.defaultValue PairId.Default
      let struct(baseCryptoCode, quoteCryptoCode) =
        pairId
      let onChainNetwork = opts.Value.GetNetwork(quoteCryptoCode)

      let refundKey = new Key()
      let preimage = RandomUtils.GetBytes 32 |> PaymentPreimage.Create

      let mutable maybeSwapId = None
      let! invoice =
        let amt = loopIn.Amount.ToLNMoney()
        let onPaymentFinished = fun (amt: Money) ->
          match maybeSwapId with
          | Some i ->
            this.Execute(i, Swap.Command.CommitReceivedOffChainPayment(amt), nameof(invoiceProvider)) :> Task
          | None ->
            // This will never happen unless they pay us unconditionally.
            Task.CompletedTask

        let onPaymentCanceled = fun (msg: string) ->
          match maybeSwapId with
          | Some i ->
            this.Execute(i, Swap.Command.MarkAsErrored(msg)) :> Task
          | None ->
            // This will never happen unless they pay us unconditionally.
            Task.CompletedTask
        invoiceProvider.GetAndListenToInvoice(baseCryptoCode, preimage, amt, loopIn.Label |> Option.defaultValue(String.Empty), onPaymentFinished, onPaymentCanceled, None)
      try
        let! inResponse =
          let req =
            { CreateSwapRequest.Invoice = invoice
              PairId = pairId
              OrderSide = OrderType.buy
              RefundPublicKey = refundKey.PubKey }
          boltzClient.CreateSwapAsync req
        let swapId = inResponse.Id |> SwapId
        maybeSwapId <- swapId |> Some
        match inResponse.Validate(invoice.PaymentHash.Value,
                                  refundKey.PubKey,
                                  loopIn.Amount,
                                  loopIn.MaxSwapFee |> ValueOption.defaultToVeryHighFee,
                                  onChainNetwork) with
        | Error e ->
          do! this.Execute(swapId, Swap.Command.MarkAsErrored(e))
          return! Error(e)
        | Ok _events ->
          let loopIn = {
            LoopIn.Id = swapId
            Status = SwapStatusType.SwapCreated
            RefundPrivateKey = refundKey
            Preimage = None
            RedeemScript = inResponse.RedeemScript
            Invoice = invoice.ToString()
            Address = inResponse.Address.ToString()
            ExpectedAmount = inResponse.ExpectedAmount
            TimeoutBlockHeight = inResponse.TimeoutBlockHeight
            LockupTransactionHex = None
            RefundTransactionId = None
            PairId = pairId
            ChainName = opts.Value.ChainName.ToString()
            Label = loopIn.Label |> Option.defaultValue String.Empty
            HTLCConfTarget =
              loopIn.HtlcConfTarget
              |> ValueOption.defaultValue Constants.DefaultHtlcConfTarget
              |> uint |> BlockHeightOffset32
            Cost = SwapCost.Zero
            MaxMinerFee =
              loopIn.Limits.MaxMinerFee
            MaxSwapFee =
              loopIn.Limits.MaxSwapFee
            LockupTransactionOutPoint = None
          }
          do! this.Execute(swapId, Swap.Command.NewLoopIn(height, loopIn))
          do!
            let swapUpdate =
              {
                Swap.Data.SwapStatusResponseData._Status = "invoice.set"
                Swap.Data.SwapStatusResponseData.Transaction = None
                Swap.Data.SwapStatusResponseData.FailureReason = None
              }
            this.Execute(swapId, Swap.Command.SwapUpdate(swapUpdate))
          let response = {
            LoopInResponse.Id = inResponse.Id
            Address = inResponse.Address
          }
          return response
      with
      | :? HttpRequestException as ex ->
        return! Error($"Error requesting to boltz ({ex.Message})")
    }
