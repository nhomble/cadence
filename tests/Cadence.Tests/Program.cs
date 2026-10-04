if (args.Length == 2 && args[0] == Cadence.Tests.LockHolder.Switch) return Cadence.Tests.LockHolder.Run(args[1]);
return Cadence.TestHarness.T.RunAll(args);
