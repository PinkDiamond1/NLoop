namespace NLoop.Server.Actors

open System
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g l =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.LightningClient = l }

type SwapActor(broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetChangeAddress,
               lightningClientProvider: ILightningClientProvider,
               opts: IOptions<NLoopOptions>,
               logger: ILogger<SwapActor>,
               eventAggregator: IEventAggregator
  ) =

  let handler =
    let aggr =
      getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress (lightningClientProvider.AsDomainLNClient())
      |> Swap.getAggregate
    Swap.getHandler aggr (opts.Value.EventStoreUrl |> Uri)

  member this.Execute(swapId, msg: Swap.Msg, ?source) = task {
    let source = source |> Option.defaultValue "SwapActor"
    let cmd =
      { Command.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = DateTime.Now } }
    match! handler.Execute swapId cmd with
    | Ok events ->
      events
      |> List.iter eventAggregator.Publish
    | Error s ->
      logger.LogError($"Error when executing swap handler %A{s}")
      eventAggregator.Publish(s)
  }

