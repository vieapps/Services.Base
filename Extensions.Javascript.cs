﻿#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using JSPool;
using JavaScriptEngineSwitcher.Core;
using JavaScriptEngineSwitcher.ChakraCore;
using Newtonsoft.Json;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services
{
	public static partial class Extensions
	{
		static Extensions()
		{
			JsEngineSwitcher.Current.DefaultEngineName = ChakraCoreJsEngine.EngineName;
			JsEngineSwitcher.Current.EngineFactories.AddChakraCore(new ChakraCoreSettings
			{
				DisableEval = true,
				EnableExperimentalFeatures = true
			});

			Extensions.JsEnginePool = new JsPool(new JsPoolConfig
			{
				MaxEngines = UtilityService.GetAppSetting("JsEngine:MaxEngines", "25").CastAs<int>(),
				MaxUsagesPerEngine = UtilityService.GetAppSetting("JsEngine:MaxUsagesPerEngine", "100").CastAs<int>(),
				GetEngineTimeout = TimeSpan.FromSeconds(UtilityService.GetAppSetting("JsEngine:GetEngineTimeout", "3").CastAs<int>())
			});

			Extensions.JsFunctions = @"
			var __toDateTime = function(value) {
				if (value !== undefined) {
					if (value instanceof Date || (typeof value === 'string' && value.trim() !== '')) {
						var date = new Date(value);
						return new DateTime(date.getFullYear(), date.getMonth(), date.getDate(), date.getHours(), date.getMinutes(), date.getSeconds(), date.getMilliseconds());
					}
					else {
						return typeof value === 'number' ? new DateTime(value) : new DateTime();
					}
				}
				else {
					return new DateTime();
				}
			};
			var __now = function() {
				return new Date().toJSON();
			};
			var __today = function() {
				var date = new Date().toJSON();
				return date.substr(0, date.indexOf('T')).replace(/\-/g, '/');
			};
			var __getAnsiUri = function(value, lowerCase) {
				return value === undefined || typeof value !== 'string' || value.trim() === '' ? '' : __sf_getAnsiUri(value, lowerCase !== undefined ? lowerCase : true);
			};
			".Replace("\t", "").Replace("\r", "").Replace("\n", " ");
		}

		static JsPool JsEnginePool { get; }

		/// <summary>
		/// Gets the common Javascript functions
		/// </summary>
		public static string JsFunctions { get; }

		/// <summary>
		/// Casts the returning value of an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsValue"></param>
		/// <returns></returns>
		public static T JsCast<T>(object jsValue)
			=> jsValue == null || jsValue is Undefined
				? default
				: jsValue is string && typeof(T).Equals(typeof(DateTime)) && (jsValue as string).Contains("T") && (jsValue as string).Contains("Z") && DateTime.TryParse(jsValue as string, out DateTime datetime)
					? datetime.CastAs<T>()
					: jsValue.CastAs<T>();

		static Func<DateTime> Func_Now => () => DateTime.Now;

		static Func<DateTime, bool, DateTime> Func_GetTimeQuarter => (time, getHighValue) => time.GetTimeQuarter(getHighValue);

		static Func<string, bool, string> Func_GetAnsiUri => (name, lowerCase) => name.GetANSIUri(lowerCase);

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="requestInfo">The object that presents the requesting information (the '__request' parameter variable)</param>
		/// <param name="object">The object that presents information of current processing object (the '__object' parameter variable and bound to 'this' instance)</param>
		/// <param name="params">The object that presents the additional parameters (the '__params' parameter variable)</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, ExpandoObject requestInfo = null, ExpandoObject @object = null, ExpandoObject @params = null)
		{
			if (!string.IsNullOrWhiteSpace(expression))
			{
				expression = expression.StartsWith("@[") && expression.EndsWith("]")
					? expression.Left(expression.Length - 1).Substring(2).Trim()
					: expression.Trim();
			}
			return Extensions.JsFunctions
				+ Environment.NewLine
				+ "(function(__request,__object,__params){__object['__evaluate']=function(){"
				+ Environment.NewLine
				+ (string.IsNullOrWhiteSpace(expression) || expression.Trim().Equals(";") ? "return undefined;" : expression)
				+ Environment.NewLine
				+ "};"
				+ Environment.NewLine
				+ "return __object.__evaluate();})"
				+ Environment.NewLine
				+ "("
				+ Environment.NewLine
				+ (requestInfo?.ToJson().ToString(Formatting.None) ?? "{}") + ","
				+ Environment.NewLine
				+ (@object?.ToJson().ToString(Formatting.None) ?? "{}") + ","
				+ Environment.NewLine
				+ (@params?.ToJson().ToString(Formatting.None) ?? "{}")
				+ ");";
		}

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="requestInfo">The object that presents the requesting information (the '__request' parameter variable)</param>
		/// <param name="object">The object that presents information of current processing object (the '__object' parameter variable and bound to 'this' instance)</param>
		/// <param name="params">The object that presents the additional parameters (the '__params' parameter variable)</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, RequestInfo requestInfo, object @object, ExpandoObject @params)
			=> expression?.GetJsExpression(requestInfo?.ToExpandoObject(), @object?.ToExpandoObject(), @params);

		/// <summary>
		/// Prepare the Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static IJsEngine PrepareJsEngine(this IJsEngine jsEngine, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			var objects = new Dictionary<string, object>(embedObjects ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
			if (!objects.ContainsKey("__sf_now"))
				objects["__sf_now"] = Extensions.Func_Now;
			if (!objects.ContainsKey("__sf_getTimeQuarter"))
				objects["__sf_getTimeQuarter"] = Extensions.Func_GetTimeQuarter;
			if (!objects.ContainsKey("__sf_getAnsiUri"))
				objects["__sf_getAnsiUri"] = Extensions.Func_GetAnsiUri;
			objects.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostObject(kvp.Key, kvp.Value));

			var types = new Dictionary<string, Type>(embedTypes ?? new Dictionary<string, Type>(), StringComparer.OrdinalIgnoreCase);
			if (!types.ContainsKey("Uri"))
				types["Uri"] = typeof(Uri);
			if (!types.ContainsKey("DateTime"))
				types["DateTime"] = typeof(DateTime);
			types.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostType(kvp.Key, kvp.Value));

			return jsEngine;
		}

		/// <summary>
		/// Creates an Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static IJsEngine CreateJsEngine(IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> JsEngineSwitcher.Current.CreateDefaultEngine().PrepareJsEngine(embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this IJsEngine jsEngine, string expression)
		{
			var jsValue = jsEngine.Evaluate(expression);
			return jsValue != null && jsValue is Undefined
				? null
				: jsValue;
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(this IJsEngine jsEngine, string expression)
			=> Extensions.JsCast<T>(jsEngine.JsEvaluate(expression));

		/// <summary>
		/// Prepare the Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static PooledJsEngine PrepareJsEngine(this PooledJsEngine jsEngine, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			jsEngine.InnerEngine.PrepareJsEngine(embedObjects, embedTypes);
			return jsEngine;
		}

		/// <summary>
		/// Gets an Javascript engine
		/// </summary>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns></returns>
		public static PooledJsEngine GetJsEngine(IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> Extensions.JsEnginePool.GetEngine().PrepareJsEngine(embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this PooledJsEngine jsEngine, string expression)
			=> jsEngine.InnerEngine.JsEvaluate(expression);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="jsEngine">The Javascript engine for evaluating an expression</param>
		/// <param name="expression">The string that presents an Javascript expression for evaluating</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(this PooledJsEngine jsEngine, string expression)
			=> jsEngine.InnerEngine.JsEvaluate<T>(expression);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object (only supported and converted to Undefied, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(string expression, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return jsEngine.JsEvaluate(expression);
			}
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents returning value from .NET objects or Javascript object</returns>
		public static T JsEvaluate<T>(string expression, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return jsEngine.JsEvaluate<T>(expression);
			}
		}

		/// <summary>
		/// Evaluates the collection of Javascript expressions
		/// </summary>
		/// <param name="expressions">The collection of Javascript expression for evaluating, each expression must end by statement 'return ..;' to return a value</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The collection of value that evaluated by the expressions</returns>
		public static IEnumerable<object> JsEvaluate(IEnumerable<string> expressions, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			using (var jsEngine = Extensions.GetJsEngine(embedObjects, embedTypes))
			{
				return expressions.Select(expression => jsEngine.JsEvaluate(expression)).ToList();
			}
		}
	}
}