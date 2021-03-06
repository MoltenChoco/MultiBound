﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Net;
using System.Net.Http;

using Fluent.IO;
using LitJson;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;

namespace MultiBound {
    public class InstanceRefreshEventArgs : EventArgs {
        public Instance selectInst { get; set; }
    }

    public class Instance {
        public static List<Instance> list = new List<Instance>();
        public static void RefreshList() {
            list.Clear();
            Config.InstanceRoot.Directories().ForEach((p) => {
                if (!p.Combine("instance.json").Exists) return; // skip
                list.Add(Load(p));
            });

            list = list.OrderBy(i => i.Name).ToList(); // todo: ThenBy
        }

        public static Instance Load(Path root) {
            Instance inst = new Instance();
            inst.path = root;

            JsonData data = JsonMapper.ToObject(root.Combine("instance.json").Read());
            inst.info = data["info"];

            return inst;
        }

        public static async void FromCollection(string url, EventHandler onComplete, EventHandler onFail, Instance updateTarget = null) {
            string id = url.Substring(url.LastIndexOf("=") + 1);

            JsonData instData;
            Path instPath;
            if (updateTarget != null) {
                instPath = updateTarget.path.Combine("instance.json");
                instData = JsonMapper.ToObject(instPath.Read());
                if (url == "") {
                    url = (string)instData["info"]["workshopLink"];
                    id = url.Substring(url.LastIndexOf("=") + 1);
                }
            }
            else {
                instPath = Config.InstanceRoot.Combine("workshop_" + id, "instance.json");
                if (instPath.Exists) instData = JsonMapper.ToObject(instPath.Read());
                else {
                    instData = JsonMapper.ToObject( // holy crepes this looks ugly
@"{
    ""info"" : { ""name"" : """", ""windowTitle"" : """" },
    ""savePath"" : ""inst:/storage/"",
    ""channel"" : ""stable"",
    ""assetSources"" : [ ""inst:/mods"" ]
}");
                }
            }

            JsonData autoMods = new JsonData();
            Dictionary<string, bool> tracking = new Dictionary<string, bool>();
            var doc = await IterateCollection(url, autoMods, tracking);
            if (doc == null) { Gtk.Application.Invoke(onFail); return; }

            // preserve all previous manually-added items
            foreach (JsonData item in instData["assetSources"]) {
                if (item.IsObject && (string)item["type"] == "workshopAuto") continue;
                autoMods.Add(item);
            }

            instData["assetSources"] = autoMods;

            string colName = doc.QuerySelector(".workshopItemDetailsHeader > .workshopItemTitle").TextContent;
            instData["info"]["workshopLink"] = url;
            if (!(instData["info"].Has("lockInfo") && (!instData["info"]["lockInfo"].IsBoolean || (bool)instData["info"]["lockInfo"]))) {
                instData["info"]["name"] = colName;
                instData["info"]["windowTitle"] = "Starbound - " + colName;
            }

            instPath.Write(JsonMapper.ToPrettyJson(instData));

            Instance.RefreshList();

            InstanceRefreshEventArgs e = new InstanceRefreshEventArgs();
            string fpath = instPath.Up().FullPath;
            foreach (Instance iInst in list) {
                if (iInst.path.FullPath == fpath) {
                    e.selectInst = iInst;
                    break;
                }
            }
            
            Gtk.Application.Invoke(null, e, onComplete);
        }

        static async Task<IHtmlDocument> IterateCollection(string url, JsonData autoMods, Dictionary<String, bool> tracking) {
            string id = url.Substring(url.LastIndexOf("=") + 1);
            if (tracking.ContainsKey(id)) return null; // already iterated this collection(!?)

            HtmlParser parser = new HtmlParser();
            HttpClient client = new HttpClient();
            var request = await client.GetAsync(url);
            var response = await request.Content.ReadAsStreamAsync();
            var doc = parser.Parse(response);

            // dispose of anything we no longer need
            parser = null;
            client.Dispose(); client = null;
            request.Dispose(); request = null;
            response.Dispose(); response = null;

            if (doc.QuerySelectorAll("a[href=\"https://steamcommunity.com/app/211820\"]").Where(m => m.TextContent == "All").Count() == 0) return null; // make sure it's for Starbound
            if (doc.QuerySelectorAll("a[onclick=\"SubscribeCollection();\"]").Count() == 0) return null; // and that it's a collection

            foreach (var item in doc.QuerySelectorAll(".collectionItemDetails > a")) { // iterate mods
                string link = item.Attributes["href"].Value;
                string modid = link.Substring(link.LastIndexOf("=") + 1);
                if (tracking.ContainsKey(modid)) continue;
                tracking[modid] = true;
                JsonData mod = new JsonData();
                mod["type"] = "workshopAuto";
                mod["id"] = modid;
                mod["friendlyName"] = item.TextContent;
                autoMods.Add(mod);
            }

            tracking[id] = true; // do this here so that if there's a circular reference somehow it doesn't herpderp everything

            foreach (var sitem in doc.QuerySelectorAll(".collectionChildren a > .workshopItemTitle")) { // iterate linked collections
                var item = sitem.ParentElement;
                await IterateCollection(item.Attributes["href"].Value, autoMods, tracking);
            }

            return doc;
        }

        private Instance() { }

        public Path path;
        public JsonData info;

        public string Name { get { return (string)info["name"]; } }
        public string WindowTitle {
            get {
                if (info.Has("windowTitle")) return (string)info["windowTitle"];
                return Name;
            }
        }
        public bool IsWorkshop {
            get {
                return info.Has("workshopLink");
            }
        }

        public string EvalPath(string pathIn) {
            int cpos = pathIn.IndexOf(':');
            if (cpos == -1) return pathIn;

            string spec = pathIn.Substring(0, cpos);
            pathIn = pathIn.Substring(cpos + 1);
            if (pathIn.StartsWith("/") || pathIn.StartsWith("\\")) pathIn = pathIn.Substring(1); // let's not send to drive root

            if (spec == "sb") return Config.StarboundRootPath.Combine(pathIn).FullPath;
            if (spec == "inst") return path.Combine(pathIn).FullPath;

            return pathIn;
        }

        public void Launch() {
            JsonData data = JsonMapper.ToObject(path.Combine("instance.json").Read());
            info = data["info"]; // might as well refresh

            JsonData initCfg = new JsonData();
            initCfg.SetJsonType(JsonType.Object);
            var dconf = initCfg["defaultConfiguration"] = new JsonData();
            dconf.SetJsonType(JsonType.Object);
            dconf["gameServerBind"] = dconf["queryServerBind"] = dconf["rconServerBind"] = "*";

            JsonData assetDirs = initCfg["assetDirectories"] = new JsonData();

            assetDirs.SetJsonType(JsonType.Array);
            assetDirs.Add("../assets/");

            Path workshopRoot = Config.StarboundRootPath.Combine("../../workshop/content/211820/");

            Dictionary<string, bool> workshop = new Dictionary<string, bool>();
            List<string> paths = new List<string>();

            Action<string> workshopAdd = (id) => {
                if (!workshop.ContainsKey(id)) workshop[id] = true;
            };
            Action<string> workshopExclude = (id) => {
                workshop[id] = false;
            };

            if (data.Has("assetSources")) {
                JsonData assetSources = data["assetSources"];
                foreach (JsonData src in assetSources) {
                    if (src.IsString) {
                        paths.Add(EvalPath((string)src)); // TODO: process with sb:, inst: markers
                        continue;
                    }

                    string type = "mod";
                    if (src.Has("type")) type = (string)src["type"];

                    switch (type) {
                        case "mod": {
                            // TODO: IMPLEMENT THIS MORE
                            if (src.Has("workshopId")) {
                                workshopAdd((string)src["workshopId"]);
                            }
                        } break;

                        case "workshopAuto": {
                            if (src.Has("id")) workshopAdd((string)src["id"]);
                        } break;

                        case "workshopExclude": {
                            if (src.Has("id")) workshopExclude((string)src["id"]);
                        } break;

                        case "workshop": {
                            Dictionary<string, bool> blacklist = new Dictionary<string, bool>();
                            if (src.Has("blacklist")) foreach (JsonData entry in src["blacklist"]) blacklist[(string)entry] = true;

                            foreach (var p in workshopRoot.Directories()) {
                                if (p.FileName.StartsWith("_")) continue; // ignore _whatever
                                if (blacklist.ContainsKey(p.FileName)) continue; // ignore blacklisted items
                                workshopAdd(p.FileName);
                            }
                        } break;

                        default: break; // unrecognized
                    }
                }
            }
            
            // build asset list
            foreach (KeyValuePair<string, bool> entry in workshop) {
                if (entry.Value) assetDirs.Add(workshopRoot.Combine(entry.Key).FullPath);
            }
            foreach (string p in paths) assetDirs.Add(p);

            // and set window title
            {
                JsonData wtpatch = new JsonData();
                wtpatch.SetJsonType(JsonType.Array);
                JsonData patchEntry = new JsonData();
                patchEntry["op"] = "replace";
                patchEntry["path"] = "/windowTitle";
                patchEntry["value"] = WindowTitle;
                wtpatch.Add(patchEntry);

                path.Combine(".autopatch").Delete(true).Combine(".autopatch/assets/client.config.patch").Write(JsonMapper.ToJson(wtpatch));
                assetDirs.Add(EvalPath("inst:/.autopatch/"));
            }

            string storageDir = "inst:/storage/";
            if (data.Has("savePath")) storageDir = (string)(data["savePath"]);
            initCfg["storageDirectory"] = EvalPath(storageDir);

            // determine which path to use
            Path sbpath = Config.StarboundPath; // default to stable channel
            if (data.Has("channel") && data["channel"].IsString && (string)data["channel"] == "unstable") {
                sbpath = Config.StarboundUnstablePath; // but have support for unstable too
            }

            Path outCfg = sbpath.Up().Combine("mbinit.config");
            outCfg.Write(JsonMapper.ToPrettyJson(initCfg));

            Process sb = new Process();
            sb.StartInfo.WorkingDirectory = sbpath.Up().FullPath;
            sb.StartInfo.FileName = sbpath.FullPath;
            sb.StartInfo.Arguments = "-bootconfig mbinit.config";
            sb.Start();
            sb.WaitForExit();

            // cleanup
            path.Combine(".autopatch").Delete(true);
            outCfg.Delete();
        }
    }
}
