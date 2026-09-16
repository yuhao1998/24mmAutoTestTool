namespace SocOtaUpgrade
{
    internal static class Log
    {
        [System.ThreadStatic]
        private static ILogSink _sink;

        public static void SetSink(ILogSink sink)
        {
            _sink = sink;
        }

        public static void Step(string message)
        {
            (_sink ?? new ConsoleLogSink()).Step(message);
        }

        public static void Error(string message)
        {
            (_sink ?? new ConsoleLogSink()).Error(message);
        }
    }
}
