using System.Linq;
using NUnit.Framework;

namespace Architector.Editor.Tests
{
    public sealed class ArchitectorDeclarationSyntaxTests
    {
        private static string Roles(string declaration)
        {
            return string.Join(" ", ArchitectorDeclarationSyntax.Classify(declaration)
                .Where(part => part.Role != ArchitectorCodeRole.Plain)
                .Select(part => $"{part.Text}:{part.Role.ToString()[0]}"));
        }

        [TestCase("public event Action Changed", "public:K event:K Action:T Changed:M")]
        [TestCase("public HeldItemSnapshot Current { get; private set; }", "public:K HeldItemSnapshot:T Current:M get:K private:K set:K")]
        [TestCase("protected override void OnAdded()", "protected:K override:K void:K OnAdded:M")]
        [TestCase("internal Boolean IsObstructed(Vector3 point, Transform receiver)", "internal:K Boolean:T IsObstructed:M Vector3:T point:P Transform:T receiver:P")]
        [TestCase("public T Get<T>(ref Dictionary<String, Int32> map, Int32 count = 5)", "public:K T:T Get:M T:T ref:K Dictionary:T String:T Int32:T map:P Int32:T count:P 5:L")]
        [TestCase("public static Boolean operator ==(Foo left, Foo right)", "public:K static:K Boolean:T operator:K =:M =:M Foo:T left:P Foo:T right:P")]
        [TestCase("public Int32 this[Int32 index] { get; set; }", "public:K Int32:T this:M Int32:T index:P get:K set:K")]
        [TestCase("public Foo(Int32 x)", "public:K Foo:M Int32:T x:P")]
        [TestCase("public void SetAim(Vector3 aimFor…", "public:K void:K SetAim:M Vector3:T aimFor:P")]
        public void ClassifiesDeclarationParts(string declaration, string expected)
        {
            Assert.AreEqual(expected, Roles(declaration));
        }

        [Test]
        public void KeepsEveryCharacter()
        {
            const string declaration = "public Dictionary<String, Int32> Map { get; }  // note \"a<b\"";
            Assert.AreEqual(declaration, string.Concat(ArchitectorDeclarationSyntax.Classify(declaration).Select(part => part.Text)));
            Assert.IsEmpty(ArchitectorDeclarationSyntax.Classify(null));
        }

        [Test]
        public void RichTextColorsPartsAndEscapesGenerics()
        {
            var rich = ArchitectorSemanticPalette.RichDeclaration("public List<Int32> Items { get; }", "property");
            StringAssert.Contains("<color=#", rich);
            StringAssert.Contains(">public</color>", rich);
            StringAssert.Contains("<​", rich);
            Assert.IsFalse(rich.Contains("<Int32"), rich);
            var memberColor = UnityEngine.ColorUtility.ToHtmlStringRGB(ArchitectorSemanticPalette.ForMemberKind("property"));
            StringAssert.Contains($"<color=#{memberColor}>Items</color>", rich);
            var baseRow = ArchitectorSemanticPalette.RichDeclaration("base · Game.UnitModule", "base");
            StringAssert.Contains($"<color=#{UnityEngine.ColorUtility.ToHtmlStringRGB(ArchitectorSemanticPalette.ForTypeKind("class"))}>Game.UnitModule</color>", baseRow);
        }
    }
}
