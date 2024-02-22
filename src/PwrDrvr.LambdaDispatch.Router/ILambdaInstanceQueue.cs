using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public interface ILambdaInstanceQueue
{
  int MaxConcurrentCount { get; }

  bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false);

  bool TryRemoveInstance([NotNullWhen(true)] out ILambdaInstance? instance);

  void AddInstance(ILambdaInstance instance);

  bool ReinstateFullInstance(ILambdaInstance instance);
}