namespace TrebuchetLib;

/// <summary>Answer of one running server to a test message sent from a server save.</summary>
/// <param name="RConConnected">false when the server runs without RCon (RCon was off when it started).</param>
public sealed record RConTestResult(int Instance, string Command, string Response, Exception? Error, bool RConConnected);
