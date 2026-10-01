using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace HallPatton
{
    /// <summary>
    /// Talks to Meta's Model API (OpenAI-compatible chat completions) to generate his
    /// answers. Uses UnityWebRequest inside a coroutine so the server's main thread never
    /// blocks waiting on the network - a stalled request must never hold up the world.
    ///
    /// Endpoint: POST https://api.meta.ai/v1/chat/completions, Bearer auth.
    /// Nothing here depends on an external NuGet package: UnityWebRequest and Newtonsoft.Json
    /// both ship with Valheim already.
    /// </summary>
    internal static class MuseClient
    {
        private const string DefaultEndpoint = "https://api.meta.ai/v1/chat/completions";

        /// <summary>
        /// Any OpenAI-compatible chat-completions endpoint. Configurable because provider choice
        /// is the thing that decides what a character is allowed to say — each provider applies
        /// its own content policy server-side, and no prompt gets around that.
        /// </summary>
        private static string Endpoint
        {
            get
            {
                string configured = Plugin.MuseEndpoint?.Value;
                return string.IsNullOrWhiteSpace(configured) ? DefaultEndpoint : configured.Trim();
            }
        }

        /// <summary>One remembered exchange, so he can follow a thread.</summary>
        internal struct Turn
        {
            public string Player;
            public string Mark;
        }

        internal static bool Configured => !string.IsNullOrEmpty(ResolveKey());

        /// <summary>
        /// Config file first, then MODEL_API_KEY. Returns null when neither supplies a key,
        /// which leaves Muse unconfigured and the built-in lines in charge. The key is never
        /// written to the log, and is deliberately never compiled into the DLL.
        /// </summary>
        private static string ResolveKey()
        {
            string fromConfig = Plugin.MuseApiKey?.Value;
            if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();

            try
            {
                string fromEnv = Environment.GetEnvironmentVariable("MODEL_API_KEY");
                if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            }
            catch
            {
                // Some sandboxed launchers deny environment access; fall through.
            }

            return null;
        }

        /// <summary>
        /// Asks for an answer. Calls <paramref name="onReply"/> with it, or
        /// <paramref name="onFailed"/> with a short reason so the caller can fall back to the
        /// built-in lines. Exactly one of the two always runs.
        /// </summary>
        internal static IEnumerator Ask(
            string playerLine,
            string context,
            IList<Turn> history,
            Action<string> onReply,
            Action<string> onFailed)
        {
            string key = ResolveKey();
            if (string.IsNullOrEmpty(key))
            {
                onFailed("no API key configured");
                yield break;
            }

            string body;
            try
            {
                body = BuildRequest(playerLine, context, history);
            }
            catch (Exception e)
            {
                onFailed("could not build request: " + e.Message);
                yield break;
            }

            using (var req = new UnityWebRequest(Endpoint, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + key);
                req.timeout = Mathf.Clamp(Plugin.MuseTimeoutSeconds.Value, 2, 60);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    // Deliberately reports the status code and not the response body, which can
                    // echo request content back into the log.
                    onFailed($"HTTP {req.responseCode} ({req.result})");
                    yield break;
                }

                string reply;
                try
                {
                    reply = ExtractReply(req.downloadHandler.text);
                }
                catch (Exception e)
                {
                    onFailed("unparseable response: " + e.Message);
                    yield break;
                }

                if (string.IsNullOrEmpty(reply)) onFailed("empty reply");
                else onReply(reply);
            }
        }

        private static string BuildRequest(string playerLine, string context, IList<Turn> history)
        {
            string system = Persona.SystemPrompt(Plugin.MaxWords.Value, Plugin.MusePersona.Value);

            var messages = new JArray
            {
                new JObject
                {
                    ["role"] = "system",
                    ["content"] = system
                }
            };

            if (history != null)
            {
                foreach (Turn t in history)
                {
                    if (!string.IsNullOrEmpty(t.Player))
                        messages.Add(new JObject { ["role"] = "user", ["content"] = t.Player });
                    if (!string.IsNullOrEmpty(t.Mark))
                        messages.Add(new JObject { ["role"] = "assistant", ["content"] = t.Mark });
                }
            }

            // Who is asking and what the world is doing rides along with the question, so it
            // can colour the answer without polluting the remembered history.
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = string.IsNullOrEmpty(context) ? playerLine : $"[{context}]\n{playerLine}"
            });

            var payload = new JObject
            {
                ["model"] = Plugin.MuseModel.Value,
                ["messages"] = messages,
                // Headroom for hidden reasoning as well as the answer — see the config comment.
                ["max_tokens"] = Mathf.Clamp(Plugin.MuseMaxTokens.Value, 64, 4000),
                ["temperature"] = Mathf.Clamp(Plugin.MuseTemperature.Value, 0f, 2f)
            };

            // Without this the model reasons until it exhausts max_tokens and returns no
            // content at all, whatever the budget. 'low' is the floor the API accepts.
            string effort = Plugin.MuseReasoningEffort.Value;
            if (!string.IsNullOrWhiteSpace(effort))
                payload["reasoning_effort"] = effort.Trim();

            return payload.ToString(Formatting.None);
        }

        /// <summary>Pulls choices[0].message.content out of an OpenAI-shaped response.</summary>
        private static string ExtractReply(string json)
        {
            JObject root = JObject.Parse(json);

            var error = root["error"];
            if (error != null && error.Type != JTokenType.Null)
                throw new Exception((string)error["message"] ?? "API returned an error");

            JToken choice = root["choices"]?[0];
            string content = (string)choice?["message"]?["content"];
            string finish = (string)choice?["finish_reason"];

            // The classic misconfiguration: reasoning ate the whole budget, so the model never
            // got round to answering. Say so plainly instead of reporting "empty reply".
            if (string.IsNullOrWhiteSpace(content) && finish == "length")
            {
                int reasoning = (int?)root["usage"]?["completion_tokens_details"]?["reasoning_tokens"] ?? 0;
                throw new Exception(
                    $"model spent all {reasoning} tokens reasoning and returned no reply - " +
                    "raise Muse.MaxTokens or set Muse.ReasoningEffort=low");
            }

            return ReplyText.Sanitize(content);
        }
    }
}
