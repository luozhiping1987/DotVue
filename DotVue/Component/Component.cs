﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DotVue
{
    /// <summary>
    /// Vue component execution
    /// </summary>
    public class Component
    {
        private readonly ComponentInfo _component;

        internal Component(ComponentInfo component)
        {
            _component = component;
        }

        #region RenderScript

        public void RenderScript(TextWriter writer)
        {
            var def = new ComponentDefinition(_component.ViewModel, _component.Content);

            // render Vue script initializer
            this.RenderScript(def, writer);
        }

        private void RenderScript(ComponentDefinition def, TextWriter writer)
        {
            writer.Write("//\n");
            writer.WriteFormat("// Component: \"{0}\"\n", _component.VPath);
            writer.Write("//\n");

            // add "style" before return Vue object
            if (def.Styles.Count > 0)
            {
                writer.WriteFormat("Vue.$addStyle('{0}');\n",
                    string.Join("", def.Styles.Select(x => x.EncodeJavascript())));
            }

            writer.Write("return {\n");

            if (def.Props.Count > 0)
            {
                writer.WriteFormat("  props: [{0}],\n", string.Join(", ", def.Props.Select(x => "'" + x.CamelCase() + "'")));
            }

            // only call Created method if created was override in component
            if (def.CreatedHook)
            {
                writer.Write("  created: function() {\n");
                writer.Write("    this.onCreated();\n");
                writer.Write("  },\n");
            }

            // append template string
            writer.WriteFormat("  template: '{0}',\n", def.Template.EncodeJavascript());
            writer.WriteFormat("  data: function() {{\n    return {0};\n  }},\n", JsonConvert.SerializeObject(def.Data, Config.JsonSettings));

            // render methods
            if (def.Methods.Count > 0)
            {
                writer.Write("  methods: {\n");

                foreach (var m in def.Methods)
                {
                    var name = m.Key;
                    var pre = m.Value.Item1;
                    var post = m.Value.Item2;
                    var parameters = m.Value.Item3;

                    writer.WriteFormat("    {0}: function({1}) {{{2}\n      this.$update(this, '{3}', [{4}]){5};\n    }}{6}\n",
                        name.CamelCase(),
                        string.Join(", ", parameters),
                        pre,
                        name,
                        string.Join(", ", parameters),
                        post.Length > 0 ? "\n          .then(function(vm) { (function() {" + post + "\n          }).call(vm); })" : "",
                        name == def.Methods.Last().Key ? "" : ",");
                }

                writer.Write("  },\n");
            }

            // render def
            if (def.Computed.Count > 0)
            {
                writer.Write("  computed: {\n");

                foreach (var c in def.Computed)
                {
                    writer.WriteFormat("    {0}: function() {{\n      {1};\n    }}{2}\n",
                        c.Key.CamelCase(),
                        c.Value,
                        c.Key == def.Computed.Last().Key ? "" : ",");
                }

                writer.Write("  },\n");
            }

            // render watchs
            if (def.Watch.Count > 0)
            {
                writer.Write("  watch: {\n");

                foreach (var w in def.Watch)
                {
                    writer.WriteFormat("    {0}: {{\n      handler: function(v, o) {{\n        if (this.$updating) return false;\n        this.{1}(v, o);\n      }},\n      deep: true\n    }}{2}\n",
                        w.Key.CamelCase(), 
                        w.Value.CamelCase(),
                        w.Key == def.Watch.Last().Key ? "" : ",");
                }

                writer.Write("  },\n");
            }

            // render scripts
            if (def.Scripts.Count > 0)
            {
                writer.Write("  mixins: [\n");

                foreach(var s in def.Scripts)
                {
                    writer.WriteFormat("(function() {{\n{0}\n}})() || {{}}{1}\n",
                        s,
                        s == def.Scripts.Last() ? "": ",");
                }

                writer.Write("  ],\n");
            }

            // render client-only properties
            writer.WriteFormat("  local: [{0}],\n", string.Join(", ", def.Locals.Select(x => "'" + x.CamelCase() + "'")));

            // add vpath to options
            writer.WriteFormat("  vpath: '{0}'\n", _component.VPath);
            writer.Write("}");
        }

        #endregion

        #region Update Models

        public void UpdateModel(string data, string props, string method, JToken[] parameters, HttpFileCollection files, TextWriter writer)
        {
            // get request json object (data + props)
            var request = JObject.Parse(data);

            request.Merge(JObject.Parse(props), Config.MergeSettings);

            var vm = (ViewModel)request.ToObject(_component.ViewModel);

            // after initialize VM, get original JObject into a single object
            var original = new JObject();

            original.Merge(JObject.FromObject(vm, Config.JSettings));

            try
            {
                // initialize final object before check changes
                var current = new JObject();
                var scripts = new StringBuilder();

                // initialize viewmodel with current request data
                ViewModel.SetData(vm, current);

                // if has method, call in existing vms
                this.ExecuteMethod(vm, current, method, parameters, files);

                // merge all scripts
                scripts.Append(ViewModel.GetClientScript(vm));

                // detect changed from original to current data and send back to browser
                var diff = this.GetDiff(original, current);

                // write changes to writer
                using (var w = new JsonTextWriter(writer))
                {
                    var output = new JObject
                    {
                        { "update", diff },
                        { "script", scripts.ToString() }
                    };
                    
                    output.WriteTo(w);
                }
            }
            finally
            {
                // dispose vm
                vm.Dispose();
            }
        }

        /// <summary>
        /// Find a method in all componenets and execute if found
        /// </summary>
        private void ExecuteMethod(ViewModel vm, JObject data, string name, JToken[] parameters, HttpFileCollection files)
        {
            MethodInfo method = null;

            var methods = _component.ViewModel
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(x => x.Name == name)
                .Where(x => x.IsFamily || x.IsPublic)
                .ToList();

            // if method not found in this compoenent, go to next compoment (plugin)
            if (methods.Count > 0)
            {
                method = methods.First();

                var pars = new List<object>();
                var index = 0;

                // convert each parameter as declared method in type
                foreach (var p in method.GetParameters())
                {
                    var token = parameters[index++];

                    if (p.ParameterType == typeof(HttpPostedFile))
                    {
                        var value = ((JValue)token).Value.ToString();

                        pars.Add(files.GetMultiple(value).FirstOrDefault());
                    }
                    else if (p.ParameterType == typeof(List<HttpPostedFile>) || p.ParameterType == typeof(IList<HttpPostedFile>))
                    {
                        var value = ((JValue)token).Value.ToString();

                        pars.Add(new List<HttpPostedFile>(files.GetMultiple(value)));
                    }
                    else if (token.Type == JTokenType.Object)
                    {
                        var obj = ((JObject)token).ToObject(p.ParameterType);

                        pars.Add(obj);
                    }
                    else if (token.Type == JTokenType.String && p.ParameterType.IsEnum)
                    {
                        var value = ((JValue)token).Value.ToString();

                        pars.Add(Enum.Parse(p.ParameterType, value));
                    }
                    else
                    {
                        var value = ((JValue)token).Value;

                        pars.Add(Convert.ChangeType(value, p.ParameterType));
                    }
                }

                // now execute method inside viewmodel
                ViewModel.Execute(vm, method, pars.ToArray());
            }

            data.Merge(JObject.FromObject(vm, Config.JSettings), Config.MergeSettings);

            if (method == null) throw new SystemException($"Method {name} do not exists, are not public/protected or has more than one signature");
        }

        /// <summary>
        /// Create a new object with only diff between original viewmodel and new changed viewmodel
        /// </summary>
        private JObject GetDiff(JObject original, JObject current)
        {
            // create a diff object to capture any change from original to current data
            var diff = new JObject();

            foreach (var item in current)
            {
                var orig = original[item.Key];

                if (orig == null && item.Value.HasValues == false) continue;

                // use a custom compare function
                if (JTokenComparer.Instance.Compare(orig, item.Value) != 0)
                {
                    diff[item.Key] = item.Value;
                }
            }

            return diff;
        }

        #endregion
    }
}
