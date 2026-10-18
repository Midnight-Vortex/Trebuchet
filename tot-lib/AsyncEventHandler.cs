namespace tot_lib;

public delegate Task AsyncEventHandler(object? sender, EventArgs args);
public delegate Task AsyncEventHandler<T>(object? sender, T args);
public delegate Task<R> AsyncEventHandler<T,R>(object? sender, T args);