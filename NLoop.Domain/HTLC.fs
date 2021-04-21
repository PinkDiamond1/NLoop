namespace NLoop.Domain

open System.Collections.Generic
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin
open NBitcoin
open NBitcoin.Crypto
open FsToolkit.ErrorHandling
open NBitcoin.DataEncoders

module Scripts =
  let reverseSwapScriptV1(preimageHash: PaymentHash) (claimPubKey: PubKey) (refundPubKey: PubKey) (timeout: BlockHeight) =
    let l = List<Op>()
    l.Add(Op.op_Implicit (OpcodeType.OP_SIZE))
    l.Add(Op.GetPushOp(32L))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUAL))
    l.Add(Op.op_Implicit (OpcodeType.OP_IF))
    l.Add(Op.op_Implicit (OpcodeType.OP_HASH160))
    l.Add(Op.GetPushOp(preimageHash.Value.ToBytes() |> Hashes.RIPEMD160))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUALVERIFY))
    l.Add(Op.GetPushOp(claimPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ELSE))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(timeout.Value |> int64))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKLOCKTIMEVERIFY))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(refundPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ENDIF))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKSIG))
    Script(l)

  let swapScriptV1 (preimageHash: PaymentHash) (claimPubKey: PubKey) (refundPubKey: PubKey) (timeout: BlockHeight)  =
    let l = List<Op>()
    l.Add(Op.op_Implicit (OpcodeType.OP_HASH160))
    l.Add(Op.GetPushOp(preimageHash.Value.ToBytes() |> Hashes.RIPEMD160))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUAL))
    l.Add(Op.op_Implicit (OpcodeType.OP_IF))
    l.Add(Op.GetPushOp(claimPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ELSE))
    l.Add(Op.GetPushOp(timeout.Value |> int64))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKLOCKTIMEVERIFY))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(refundPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ENDIF))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKSIG))
    Script(l)

  let isSwapScriptV1 (_sc: Script) =
    failwith "TODO"

  let private checkOpcode (os: Op []) index expected =
    (os.[index].Code = expected)
    |> Result.requireTrue $"The {index}th opcode must be {expected}, it was {os.[index].Code}"
  let private hex =  HexEncoder()
  let private checkPushData (os: Op []) index expected =
    (Utils.ArrayEqual(os.[index].PushData, expected))
    |> Result.requireTrue
      $"The push {index}the opcode's pushdata must be {expected |> hex.EncodeData} it was {os.[index].PushData |> hex.EncodeData}"
  let validateReverseSwapScript (preimageHash: uint256) (script: Script) =
    let os = script.ToOps() |> Seq.toArray
    let checkOpcode = checkOpcode os
    let checkPushData = checkPushData os
    result {
      do! checkOpcode 0 OpcodeType.OP_SIZE

      do! checkOpcode 2 OpcodeType.OP_EQUAL
      do! checkOpcode 3 OpcodeType.OP_IF
      do! checkOpcode 4 OpcodeType.OP_HASH160
      do! checkPushData 5 (preimageHash.ToBytes() |> Hashes.RIPEMD160)
      do! checkOpcode 6 OpcodeType.OP_EQUALVERIFY

      do! checkOpcode 8 OpcodeType.OP_ELSE
      do! checkOpcode 9 OpcodeType.OP_DROP

      do! checkOpcode 11 OpcodeType.OP_CHECKLOCKTIMEVERIFY
      do! checkOpcode 12 OpcodeType.OP_DROP

      do! checkOpcode 14 OpcodeType.OP_ENDIF
      do! checkOpcode 15 OpcodeType.OP_CHECKSIG
    }


  let validateSwapScript (preimageHash: uint256) (script: Script) =
    let os = script.ToOps() |> Seq.toArray
    let checkOpcode = checkOpcode os
    let checkPushData = checkPushData os
    result {
      do! checkOpcode 0 OpcodeType.OP_HASH160
      do! checkPushData 1 (preimageHash.ToBytes() |> Hashes.RIPEMD160)
      do! checkOpcode 2 OpcodeType.OP_EQUAL
      do! checkOpcode 3 OpcodeType.OP_IF

      do! checkOpcode 5 OpcodeType.OP_ELSE

      do! checkOpcode 7 OpcodeType.OP_CHECKLOCKTIMEVERIFY
      do! checkOpcode 8 OpcodeType.OP_DROP

      do! checkOpcode 10 OpcodeType.OP_ENDIF
      do! checkOpcode 11 OpcodeType.OP_CHECKSIG
    }

type HTLC = {
  HTLCScript: Script
}
