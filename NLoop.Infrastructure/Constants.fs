namespace NLoop.Infrastructure

open System
open System.IO

[<RequireQualifiedAccess>]
module Constants =
  let HomePath =
     if (Environment.OSVersion.Platform = PlatformID.Unix || Environment.OSVersion.Platform = PlatformID.MacOSX)
     then Environment.GetEnvironmentVariable("HOME")
     else Environment.GetEnvironmentVariable("%HOMEDRIVE%%HOMEPATH%")
     |> fun h -> if (isNull h) then raise <| Exception("Failed to define home directory path") else h

  [<Literal>]
  let HomeDirectoryName = ".nloop"
  let HomeDirectoryPath = Path.Join(HomePath, HomeDirectoryName)

  [<Literal>]
  let DefaultHttpsPort = 443

  let DefaultHttpsCertFile = Path.Combine(HomePath, ".aspnet", "https", "ssl.cert")
