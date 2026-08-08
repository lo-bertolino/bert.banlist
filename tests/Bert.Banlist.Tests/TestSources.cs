namespace Bert.Banlist.Tests
{
    /// <summary>Shared "third-party library" definitions compiled into every test.</summary>
    internal static class TestSources
    {
        public const string Definitions = """
            namespace Legacy.Stuff
            {
                public class OldHelper
                {
                    public OldHelper() { }
                    public OldHelper(int x) { }
                    public static void DoThing(string s) { }
                    public static void DoThing(int i) { }
                    public static string Value { get; set; }
                    public static int Count;
                }

                public class OldList<T> { }

                public class OldLogger
                {
                    public static void Log(string s) { }
                }
            }

            namespace Legacy.Data
            {
                public class Client { }
            }

            namespace New.Stuff
            {
                public class NewHelper
                {
                    public NewHelper() { }
                    public NewHelper(int x) { }
                    public static void DoThing(string s) { }
                    public static void DoThing(int i) { }
                    public static string Value { get; set; }
                    public static int Count;
                }

                public class NewList<T> { }

                public class NewLogger
                {
                    public static void Log(string s) { }
                }

                public class NoIntCtor
                {
                    public NoIntCtor() { }
                }
            }

            namespace New.Data
            {
                public class Client { }
            }
            """;
    }
}
