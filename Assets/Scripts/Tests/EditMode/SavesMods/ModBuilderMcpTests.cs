// ModBuilderMcpTests.cs
// Feature: CLAUDE.md "Mods" > Mod Builder MCP server, and the quest format for AIs (Events.QuestSchema, written to
// Modding/ai/gof2-modding/quest.schema.json / quests.md by GoF2 > Build > Modding AI Reference). The schema has a step
// definition for every row of QuestSteps, with every key a new step of that type has; the markdown names every step type.
// The server's JSON-RPC (ModBuilderMcp.Dispatch, without the HTTP layer or the main-thread pump): initialize echoes a known
// protocol version and gives a session, ping answers, tools/list lists every tool with its annotations (the ones that change
// the project aren't read-only, removal is destructive), an unknown tool or method is a JSON-RPC error, a notification gets
// no answer.

using System.Linq;
using System.Reflection;
using GoF2Remake.Events;
using GoF2Remake.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace GoF2Remake.Tests
{
    public class ModBuilderMcpTests
    {
        static JObject StepSchema(JObject schema, string type) =>
            ((JArray)schema["$defs"]["step"]["oneOf"]).OfType<JObject>().FirstOrDefault(s => (string)s["properties"]?["type"]?["const"] == type);

        [Test]
        public void SchemaHasEveryStepTypeAndItsKeys()
        {
            var schema = QuestSchema.Build();
            Assert.AreEqual("gof2quest", (string)schema["properties"]["format"]["const"]);
            foreach (var t in QuestSteps.Types)
            {
                var def = StepSchema(schema, t.type);
                Assert.IsNotNull(def, $"no schema for step '{t.type}'");
                var props = (JObject)def["properties"];
                foreach (var key in QuestSteps.New(t.type).Properties().Select(p => p.Name))
                    Assert.IsNotNull(props[key], $"step '{t.type}': its key '{key}' isn't in the schema");
            }
        }

        [Test]
        public void MarkdownNamesEveryStepType()
        {
            string md = QuestSchema.Markdown();
            foreach (var t in QuestSteps.Types)
                StringAssert.Contains("`" + t.type + "`", md, $"quests.md misses '{t.type}'");
        }

        static JObject Dispatch(JObject message, out string session)
        {
            var m = typeof(ModBuilderMcp).GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { message, null };
            var answer = (JObject)m.Invoke(null, args);
            session = (string)args[1];
            return answer;
        }

        static JObject Rpc(string method, JObject parameters = null, int? id = 1) =>
            new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters ?? new JObject(), ["id"] = id.HasValue ? (JToken)id.Value : null };

        [Test]
        public void InitializeEchoesAKnownVersionAndGivesASession()
        {
            var a = Dispatch(Rpc("initialize", new JObject { ["protocolVersion"] = "2025-03-26", ["clientInfo"] = new JObject { ["name"] = "test", ["version"] = "1" } }), out string session);
            Assert.AreEqual("2025-03-26", (string)a["result"]["protocolVersion"]);
            Assert.IsNotNull(a["result"]["capabilities"]["tools"]);
            Assert.IsFalse(string.IsNullOrEmpty((string)a["result"]["instructions"]));
            Assert.IsFalse(string.IsNullOrEmpty(session));

            var b = Dispatch(Rpc("initialize", new JObject { ["protocolVersion"] = "1999-01-01" }), out _);
            Assert.AreEqual("2025-06-18", (string)b["result"]["protocolVersion"], "an unknown version gets the newest");
        }

        [Test]
        public void ToolsListHasEveryToolWithItsAnnotations()
        {
            var a = Dispatch(Rpc("tools/list"), out _);
            var tools = ((JArray)a["result"]["tools"]).OfType<JObject>().ToDictionary(t => (string)t["name"]);
            foreach (var name in new[] { "builder_status", "search_content", "list_entries", "get_entries", "put_entry", "delete_entry",
                                         "list_files", "read_file", "write_file", "compile_quest", "validate", "get_quest_schema",
                                         "play_test", "end_play_test", "get_log", "screenshot", "undo", "redo", "show_kind" })
            {
                Assert.IsTrue(tools.ContainsKey(name), $"missing tool {name}");
                Assert.AreEqual("object", (string)tools[name]["inputSchema"]["type"], name);
            }
            foreach (var name in new[] { "put_entry", "delete_entry", "write_file", "undo", "redo", "play_test" })
                Assert.IsFalse((bool)tools[name]["annotations"]["readOnlyHint"], $"{name} changes the project");
            foreach (var name in new[] { "builder_status", "search_content", "get_entries", "read_file", "validate", "get_log" })
                Assert.IsTrue((bool)tools[name]["annotations"]["readOnlyHint"], $"{name} only reads");
            Assert.IsTrue((bool)tools["delete_entry"]["annotations"]["destructiveHint"]);
        }

        [Test]
        public void PingUnknownMethodsAndNotifications()
        {
            Assert.IsNotNull(Dispatch(Rpc("ping"), out _)["result"]);
            Assert.AreEqual(-32601, (int)Dispatch(Rpc("no/such"), out _)["error"]["code"]);
            Assert.AreEqual(-32602, (int)Dispatch(Rpc("tools/call", new JObject { ["name"] = "no_such_tool" }), out _)["error"]["code"]);
            var note = new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" };
            Assert.IsNull(Dispatch(note, out _), "a notification has no answer");
        }
    }
}
