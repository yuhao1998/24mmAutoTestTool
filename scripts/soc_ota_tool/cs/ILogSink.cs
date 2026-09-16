namespace SocOtaUpgrade
{
    internal interface ILogSink
    {
        void Step(string message);
        void Error(string message);
    }

    internal sealed class ConsoleLogSink : ILogSink
    {
        public void Step(string message)
        {
            System.Console.WriteLine("[" + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message);
        }

        public void Error(string message)
        {
            var prev = System.Console.ForegroundColor;
            System.Console.ForegroundColor = System.ConsoleColor.Red;
            System.Console.WriteLine("[ERROR] " + message);
            System.Console.ForegroundColor = prev;
        }
    }
}
