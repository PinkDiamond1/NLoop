namespace NLoop.Server

open FsToolkit.ErrorHandling
open System.Text.Json
open DotNetLightning.Utils
open Giraffe
open LndClient
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.SwapServerClient

[<AutoOpen>]
module CustomHandlers =
  type SSEEvent = {
    Name: string
    Data: obj
    Id: string
    Retry: int option
  }

  type HttpContext with
    member this.SetBlockHeight(cc, height: uint64) =
      this.Items.Add($"{cc}-BlockHeight", BlockHeight(uint32 height))
    member this.GetBlockHeight(cc: SupportedCryptoCode) =
      match this.Items.TryGetValue($"{cc}-BlockHeight") with
      | false, _ -> failwithf "Unreachable! could not get block height for %A" cc
      | true, v -> v :?> BlockHeight

  let inline internal error503 e =
    setStatusCode StatusCodes.Status503ServiceUnavailable
      >=> json {| error = e.ToString() |}

  let inline internal errorBadRequest (errors: #seq<string>) =
    setStatusCode StatusCodes.Status400BadRequest
      >=> json {| errors = errors |}

  let internal checkBlockchainIsSyncedAndSetTipHeight(cryptoCodePair: PairId) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {

      let getClient = ctx.GetService<GetBlockchainClient>()
      let ccs =
        let struct (ourCryptoCode, theirCryptoCode) = cryptoCodePair.Value
        [ourCryptoCode; theirCryptoCode] |> Seq.distinct
      let mutable errorMsg = null
      for cc in ccs do
        let rpcClient = getClient(cc)
        let! info = rpcClient.GetBlockChainInfo()
        ctx.SetBlockHeight(cc, info.Height.Value |> uint64)
        if info.Progress < 1.f then
          errorMsg <- $"{cc} blockchain is not synced. VerificationProgress: %f{info.Progress}. Please wait until its done."
        else
          ()

      if (errorMsg |> isNull) then
        return! next ctx
      else
        return! error503 errorMsg next ctx
    }

  open System.Threading.Tasks
  let internal checkWeHaveRouteToCounterParty(offChainCryptoCode: SupportedCryptoCode) (amt: Money) (chanIdsSpecified: ShortChannelId[]) =
    fun (next: HttpFunc) ( ctx: HttpContext) -> task {
      let cli = ctx.GetService<ILightningClientProvider>().GetClient(offChainCryptoCode)
      let boltzCli = ctx.GetService<ISwapServerClient>()
      let nodesT = boltzCli.GetNodes()
      let! nodes = nodesT
      let mutable maybeResult = None
      let logger =
        ctx
          .GetLogger<_>()
      for kv in nodes.Nodes do
        if maybeResult.IsSome then () else
        try
          let! r  = cli.QueryRoutes(kv.Value.NodeKey, amt.ToLNMoney())
          if (r.Value.Length > 0) then
            maybeResult <- Some r
        with
        | ex ->
          logger
            .LogError $"{ex}"

      if maybeResult.IsNone then
        return! error503 $"Failed to find route to Boltz server. Make sure the channel is open and active" next ctx
      else
        let chanIds =
          maybeResult.Value.Value
          |> List.head
          |> fun firstRoute -> firstRoute.ShortChannelId
        logger.LogDebug($"paying through the channel {chanIds} ({chanIds.ToUInt64()})")
        return! next ctx
    }

  let internal validateFeeLimitAgainstServerQuote(req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let swapServerClient = ctx.GetService<ISwapServerClient>()
      let! quote =
        let r = { SwapDTO.LoopOutQuoteRequest.Amount = req.Amount
                  SwapDTO.SweepConfTarget =
                    req.SweepConfTarget
                    |> ValueOption.map (uint32 >> BlockHeightOffset32)
                    |> ValueOption.defaultValue req.PairIdValue.DefaultLoopOutParameters.SweepConfTarget
                  SwapDTO.Pair = req.PairIdValue }
        swapServerClient.GetLoopOutQuote(r)
      let r =
        quote.Validate(req.Limits)
        |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! errorBadRequest [e] next ctx
      | Ok () -> return! next ctx
  }

  let internal validateLoopInFeeLimitAgainstServerQuote(req: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let swapServerClient = ctx.GetService<ISwapServerClient>()
      let! quote =
        let r = {
          SwapDTO.LoopInQuoteRequest.Amount = req.Amount
          SwapDTO.LoopInQuoteRequest.Pair = pairId
        }
        swapServerClient.GetLoopInQuote(r)
      let r = quote.Validate(req.Limits) |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! errorBadRequest [e] next ctx
      | Ok () -> return! next ctx
  }
