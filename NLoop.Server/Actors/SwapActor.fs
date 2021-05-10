namespace NLoop.Server.Actors

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g l =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.LightningClient = l }

type SwapActor(logger: ILoggerFactory,
               broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetChangeAddress,
               lightningClientProvider: ILightningClientProvider,
               eventAggregator: EventAggregator) =
  inherit Actor<Swap.State, Swap.Msg, Swap.Event, Swap.Error>
    ({ Zero = Swap.State.Zero
       Apply = Swap.applyChanges
       Exec = Swap.executeCommand (getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress (lightningClientProvider.AsDomainLNClient())) },
       logger.CreateLogger<SwapActor>())

  let logger = logger.CreateLogger<SwapActor>()
  override this.HandleError(error) =
    logger.LogError($"{error}")
    Task.CompletedTask
  override this.PublishEvent(evt) =
    logger.LogDebug($"{evt}")
    eventAggregator.Publish(evt)
    Task.CompletedTask

