#region Related components
using System;
using System.Linq;
using System.Dynamic;
using System.Collections.Generic;
using JSPool;
using JavaScriptEngineSwitcher.Core;
using JavaScriptEngineSwitcher.ChakraCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
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
			var __toDateTime = function (value) {
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
			var __now = function () {
				return new Date().toJSON();
			};
			var __today = function () {
				var date = new Date().toJSON();
				return date.substring(0, date.indexOf('T')).replace(/\-/g, '/');
			};
			var __getAnsiUri = function (value, lowerCase) {
				return value === undefined || typeof value !== 'string' || value.trim() === '' ? '' : __sf_GetAnsiUri(value, lowerCase !== undefined ? !!lowerCase : true);
			};
			".Replace("\t", "").Replace("\r", "").Replace("\n", " ");
		}

		static Func<DateTime> Func_Now => () => DateTime.Now;

		static Func<string, bool, string> Func_GetAnsiUri => (name, lowerCase) => name.GetANSIUri(lowerCase);

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
				: jsValue is string jsStr && typeof(T).Equals(typeof(DateTime)) && jsStr.Contains("T") && jsStr.Contains("Z") && DateTime.TryParse(jsStr, out var datetime)
					? datetime.CastAs<T>()
					: jsValue.CastAs<T>();

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, JToken @object, JToken requestInfo = null, JToken @params = null)
		{
			expression = !string.IsNullOrWhiteSpace(expression) && expression.StartsWith("@[") && expression.EndsWith("]")
				? expression.Left(expression.Length - 1).Substring(2).Trim()
				: (expression ?? "").Trim();
			return Extensions.JsFunctions
				+ Environment.NewLine
				+ $"var __object = {@object?.ToString(Formatting.None) ?? "{}"};"
				+ Environment.NewLine
				+ "__object.__evaluate = function (__request, __params) {"
				+ Environment.NewLine
				+ (string.IsNullOrWhiteSpace(expression) || expression.Equals(";") ? "return undefined;" : $"{(expression.IndexOf("return") < 0 ? "return " : "")}{expression}{(expression.EndsWith(";") ? "" : ";")}")
				+ Environment.NewLine
				+ "};"
				+ Environment.NewLine
				+ $"__object.__evaluate({requestInfo?.ToString(Formatting.None) ?? "{}"}, {@params?.ToString(Formatting.None) ?? "{}"});";
		}

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null)
			=> expression?.GetJsExpression(@object?.ToJson(), requestInfo?.ToJson(), @params?.ToJson());

		/// <summary>
		/// Gets the Javascript expression for evaluating
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <returns></returns>
		public static string GetJsExpression(this string expression, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null)
			=> expression?.GetJsExpression(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value from .NET objects or Javascript object (only supported and converted to Undefined, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this string expression, JToken @object, JToken requestInfo = null, JToken @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
		{
			if (!string.IsNullOrWhiteSpace(expression))
				using (var jsEngine = Extensions.JsEnginePool.GetEngine())
				{
					var objects = new Dictionary<string, object>(embedObjects ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase)
					{
						["__sf_Now"] = Extensions.Func_Now,
						["__sf_GetAnsiUri"] = Extensions.Func_GetAnsiUri
					};
					objects.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostObject(kvp.Key, kvp.Value));
					var types = new Dictionary<string, Type>(embedTypes ?? new Dictionary<string, Type>(), StringComparer.OrdinalIgnoreCase)
					{
						["Uri"] = typeof(Uri),
						["DateTime"] = typeof(DateTime),
					};
					types.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null).ForEach(kvp => jsEngine.EmbedHostType(kvp.Key, kvp.Value));
					var jsValue = jsEngine.Evaluate(expression.GetJsExpression(@object, requestInfo, @params));
					return jsValue is Undefined ? null : jsValue;
				}
			return null;
		}

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value from .NET objects or Javascript object (only supported and converted to Undefined, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this string expression, ExpandoObject @object, ExpandoObject requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> expression?.JsEvaluate(@object?.ToJson(), requestInfo?.ToJson(), @params?.ToJson(), embedObjects, embedTypes);

		/// <summary>
		/// Evaluates an Javascript expression
		/// </summary>
		/// <param name="expression">The string that presents an Javascript expression for evaluating, the expression must end by statement 'return ..;' to return a value</param>
		/// <param name="object">The object that presents information of current processing object (the variable named as '__object' and bound to 'this' instance)</param>
		/// <param name="requestInfo">The object that presents the requesting information (the variable named as '__request')</param>
		/// <param name="params">The object that presents the additional parameters (the variable named as '__params')</param>
		/// <param name="embedObjects">The collection that presents objects are embed as global variables, can be simple classes (generic is not supported), strucs or delegates</param>
		/// <param name="embedTypes">The collection that presents objects are embed as global types</param>
		/// <returns>The object that presents the returning value from .NET objects or Javascript object (only supported and converted to Undefined, Boolean, Int, Double and String)</returns>
		public static object JsEvaluate(this string expression, object @object = null, RequestInfo requestInfo = null, ExpandoObject @params = null, IDictionary<string, object> embedObjects = null, IDictionary<string, Type> embedTypes = null)
			=> expression?.JsEvaluate(@object is IBusinessEntity bizObject ? bizObject.ToExpandoObject() : @object?.ToExpandoObject(), requestInfo?.AsExpandoObject, @params, embedObjects, embedTypes);
	}
}