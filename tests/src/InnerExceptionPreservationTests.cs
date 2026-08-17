using System;
using System.Collections.Generic;
using NLua;
using NLua.Exceptions;
using NUnit.Framework;

namespace NLuaTest
{
    /// <summary>
    /// Pins that <see cref="LuaScriptException.InnerException"/> is preserved
    /// when a CLR indexer invocation (<c>get_Item</c>) throws while resolving
    /// a member-style or bracket-style access from Lua. Before this fix,
    /// <c>Metatables.cs</c> <c>TryIndexMethods</c> caught the
    /// <see cref="System.Reflection.TargetInvocationException"/> and rethrew
    /// via <c>ThrowError(luaState, "key '...' not found")</c> — the string
    /// overload — which built the <see cref="LuaScriptException"/> without an
    /// inner. C#-side callers wanting to route on the original CLR exception
    /// type (e.g. <see cref="KeyNotFoundException"/>) could only string-match
    /// the message, which is brittle and indistinguishable from any other
    /// Lua-side message that happens to contain the same substring.
    ///
    /// The fix adds a new
    /// <c>LuaScriptException(string message, string source, Exception innerException)</c>
    /// constructor and a matching
    /// <c>ObjectTranslator.ThrowError(LuaState, string, Exception)</c>
    /// overload, then updates both indexer-catch sites in Metatables
    /// (<c>TryGetValueForKeyMethods</c> and <c>TryIndexMethods</c>) to route
    /// the original CLR exception through as the inner.
    /// </summary>
    [TestFixture]
    public class InnerExceptionPreservationTests
    {
        // Custom exception used to prove routing works for arbitrary CLR
        // exception types, not just KeyNotFoundException.
        private class CustomIndexerException : InvalidOperationException
        {
            public CustomIndexerException(string message) : base(message) { }
        }

        private class ThrowingIndexer
        {
            public object this[string key] => throw new CustomIndexerException("custom: " + key);
        }

        [Test]
        public void GetItem_AbsentKey_KeyNotFoundException_IsPreservedAsInner()
        {
            using (Lua lua = new Lua())
            {
                var dict = new Dictionary<string, object>();
                dict["present"] = 1;
                lua["d"] = dict;

                var ex = Assert.Throws<LuaScriptException>(() =>
                    lua.DoString("return d['absent']"));

                Assert.That(ex.Message, Does.Contain("absent").And.Contain("not found"),
                    "the readable message is preserved (nice diagnostic string is what users see in the Lua error)");
                Assert.That(ex.IsNetException, Is.True,
                    "IsNetException is set when the exception carries a CLR-side cause");
                Assert.That(ex.InnerException, Is.InstanceOf<KeyNotFoundException>(),
                    "the KeyNotFoundException from Dictionary.get_Item is preserved as InnerException so callers can route on the CLR type");
            }
        }

        [Test]
        public void GetItem_AbsentKey_MemberSyntax_KeyNotFoundIsPreserved()
        {
            // Member-style `d.absent` resolves through the same TryIndexMethods
            // code path as bracket-style `d['absent']` — pin both so a future
            // refactor doesn't split them.
            using (Lua lua = new Lua())
            {
                var dict = new Dictionary<string, object>();
                dict["present"] = 1;
                lua["d"] = dict;

                var ex = Assert.Throws<LuaScriptException>(() =>
                    lua.DoString("return d.absent"));

                Assert.That(ex.InnerException, Is.InstanceOf<KeyNotFoundException>(),
                    "member-syntax access must preserve the InnerException identically to bracket-syntax");
            }
        }

        [Test]
        public void GetItem_CustomIndexerException_IsPreservedAsInner()
        {
            // The non-KeyNotFoundException branch of the same catch block
            // (exception indexing '<key>' <message>) also has to preserve
            // the original exception — otherwise callers routing on any
            // custom CLR exception type are still blind.
            using (Lua lua = new Lua())
            {
                lua["obj"] = new ThrowingIndexer();

                var ex = Assert.Throws<LuaScriptException>(() =>
                    lua.DoString("return obj['x']"));

                Assert.That(ex.Message, Does.Contain("exception indexing").And.Contain("'x'"),
                    "readable message shape is preserved on the non-KeyNotFound branch");
                Assert.That(ex.IsNetException, Is.True);
                Assert.That(ex.InnerException, Is.InstanceOf<CustomIndexerException>(),
                    "the original CustomIndexerException is preserved as InnerException");
                Assert.That(ex.InnerException!.Message, Does.Contain("custom:"),
                    "the InnerException's own message is intact — no modification, just carried");
            }
        }

        [Test]
        public void LuaScriptException_NewCtor_SetsMessageSourceAndInner()
        {
            // Direct unit coverage of the new (string, string, Exception)
            // constructor added on LuaScriptException — pins the shape the
            // ObjectTranslator + Metatables sites depend on.
            var inner = new InvalidOperationException("some CLR failure");
            var ex = new LuaScriptException("readable message", "source-location", inner);

            Assert.That(ex.Message, Is.EqualTo("readable message"));
            Assert.That(ex.Source, Is.EqualTo("source-location"));
            Assert.That(ex.InnerException, Is.SameAs(inner));
            Assert.That(ex.IsNetException, Is.True);
        }
    }
}
