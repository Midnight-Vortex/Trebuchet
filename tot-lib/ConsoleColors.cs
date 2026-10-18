namespace tot_lib;

public static class ConsoleColors
{
    public static readonly string NL          = Environment.NewLine; // shortcut
    public static readonly string NORMAL      = "\x1b[39m";
    public static readonly string RED         = "\x1b[91m";
    public static readonly string GREEN       = "\x1b[92m";
    public static readonly string YELLOW      = "\x1b[93m";
    public static readonly string BLUE        = "\x1b[94m";
    public static readonly string MAGENTA     = "\x1b[95m";
    public static readonly string CYAN        = "\x1b[96m";
    public static readonly string GREY        = "\x1b[97m";
    public static readonly string BOLD        = "\x1b[1m";
    public static readonly string NOBOLD      = "\x1b[22m";
    public static readonly string UNDERLINE   = "\x1b[4m";
    public static readonly string NOUNDERLINE = "\x1b[24m";
    public static readonly string REVERSE     = "\x1b[7m";
    public static readonly string NOREVERSE   = "\x1b[27m";
}