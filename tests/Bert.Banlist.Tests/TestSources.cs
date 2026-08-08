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
                    public OldHelper(int x, string y) { }
                    public static void DoThing(string s) { }
                    public static void DoThing(int i) { }
                    public static string Value { get; set; }
                    public static int Count;
                    public static string Combine(string a, string b) => a + b;
                    public static string TrimTo(string s, int length) => s.Substring(0, length);
                }

                public class OldList<T> { }

                public class OldLogger
                {
                    public static void Log(string s) { }
                }

                public class InstanceWidget : New.Stuff.IInstanceWidget
                {
                    public void OldInstanceMethod(string s) { }
                    public string OldInstanceValue { get; set; }
                    public void NewInstanceMethod(string s) { }
                    public string NewInstanceValue { get; set; }
                }

                public class InstanceWidgetDerived : New.Stuff.InstanceWidgetBase
                {
                    public void OldInstanceMethod2(string s) { }
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
                    public NewHelper(string y, int x) { }
                    public static void DoThing(string s) { }
                    public static void DoThing(int i) { }
                    public static string Value { get; set; }
                    public static int Count;
                    public static string Combine(string a, string b) => string.Empty;
                    public static string TrimTo(string s) => string.Empty;
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

                public interface IInstanceWidget
                {
                    void NewInstanceMethod(string s);
                    string NewInstanceValue { get; set; }
                }

                public class InstanceWidgetBase
                {
                    public void NewInstanceMethod2(string s) { }
                }
            }

            namespace New.Data
            {
                public class Client { }
            }
            """;
    }
}
