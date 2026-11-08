#region Related components
using System;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using net.vieapps.Components.Utility;
using ServiceExtensions = net.vieapps.Services.Extensions;
#endregion

namespace net.vieapps.Services.AI
{
	/// <summary>
	/// Extensions for working with AI
	/// </summary>
	public static class Extensions
	{
		#region Request preparation
		// Initialization is shared and independent of any caller's cancellation token.
		// Run synchronous configuration loading and provider initialization off the caller's thread.
		/// <summary>
		/// Shares lazy provider initialization independently of any caller cancellation token
		/// </summary>
		static readonly Lazy<Task<ProviderRegistry>> Registry = new Lazy<Task<ProviderRegistry>>(() => Task.Run(ProviderRegistry.LoadAsync));

		/// <summary>
		/// Executes one logical AI request with canonical validation, bounded retries and shared observability
		/// </summary>
		/// <param name="requestInfo">The existing business request supplying execution context and overrides</param>
		/// <param name="message">The input, explicit capability scope, expected schema and execution options</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>The normalized response with aggregated usage from all provider attempts</returns>
		/// <remarks>The input message is copied. Mutating business operations remain responsible for idempotency across retries.</remarks>
		/// <exception cref="ArgumentNullException">The request context or message is null</exception>
		/// <exception cref="OperationCanceledException">The caller cancels execution</exception>
		/// <exception cref="ProviderException">Configuration, transport or provider execution fails</exception>
		/// <exception cref="UnsupportedCapabilityException">The selected provider or business service does not support a requested capability</exception>
		/// <exception cref="StructuredOutputException">The output does not satisfy the canonical schema</exception>
		public static async Task<ResponseMessage> ExecuteAsync(this RequestInfo requestInfo, RequestMessage message, CancellationToken cancellationToken = default)
		{
			if (requestInfo == null)
				throw new ArgumentNullException(nameof(requestInfo));
			if (message == null)
				throw new ArgumentNullException(nameof(message));
			cancellationToken.ThrowIfCancellationRequested();

			return await ExecuteLogicalAsync(requestInfo, message, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Waits for shared provider initialization while honoring cancellation of this wait
		/// </summary>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>The shared initialized provider registry</returns>
		/// <remarks>Canceling a wait does not cancel shared initialization. An initialization failure remains on the shared task.</remarks>
		static async Task<ProviderRegistry> GetRegistryAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var registryTask = Registry.Value;
			try
			{
				// The non-generic overload waits without reading Task<T>.Result (which wraps failures).
				if (cancellationToken.CanBeCanceled)
					await ((Task)registryTask).WithCancellationToken(cancellationToken).ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				var registry = await registryTask.ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				return registry;
			}
			catch
			{
				// Caller cancellation has priority even when initialization faults at the same time.
				cancellationToken.ThrowIfCancellationRequested();
				throw;
			}
		}

		/// <summary>
		/// Creates the effective request and materializes its canonical schema before provider attempts
		/// </summary>
		/// <param name="message">The original request, which is not modified</param>
		/// <param name="info">The selected provider profile supplying default options and model</param>
		/// <param name="requestInfo">The business request supplying the final execution overrides</param>
		/// <returns>An independent request snapshot without a schema factory</returns>
		/// <remarks>Profile defaults are overridden by message options, then RequestInfo.Extra. SchemaFactory is invoked once only when Schema is absent.</remarks>
		internal static RequestMessage Prepare(RequestMessage message, ProviderInfo info, RequestInfo requestInfo)
		{
			// Model follows the same precedence as other execution options:
			// ProviderInfo defaults -> RequestMessage.Options -> RequestInfo.Extra.
			var options = new JObject();
			if (info.Options != null)
				foreach (var option in info.Options)
				{
					JToken value = new JValue(option.Value);
					// XML profile values are strings; native JSON options may contain numbers, booleans or objects.
					if (!Provider.ExecutionOptions.Contains(option.Key) && option.Value != null)
						try { value = Provider.ParseJson(option.Value); }
						catch (JsonException) { }
					options[Provider.ExecutionOptions.Contains(option.Key) ? option.Key.ToLowerInvariant() : option.Key] = value;
				}
			if (!string.IsNullOrWhiteSpace(info.Model))
				options["model"] = info.Model;
			if (message.Options != null)
				foreach (var option in message.Options.Properties())
					options[Provider.ExecutionOptions.Contains(option.Name) ? option.Name.ToLowerInvariant() : option.Name] = option.Value.Clone(onError: exception => throw exception);
			foreach (var hint in new[] { "model", "timeout", "request-timeout" })
			{
				var value = GetHint(requestInfo, $"x-ai-{hint}");
				if (!string.IsNullOrWhiteSpace(value))
					options[hint] = value;
			}

			// Materialize once here, before any future attempt loop. Do not carry the factory forward.
			var schema = message.Schema ?? message.SchemaFactory?.Invoke();
			return CopyMessage(message, schema, options);
		}

		/// <summary>
		/// Copies a request with explicit schema and options snapshots
		/// </summary>
		/// <param name="message">The request whose instructions, input, references and capability scope are copied</param>
		/// <param name="schema">The canonical schema to clone, or null for unstructured output</param>
		/// <param name="options">The non-null effective options to clone</param>
		/// <returns>A request copy without a schema factory</returns>
		static RequestMessage CopyMessage(RequestMessage message, JObject schema, JObject options)
			=> new RequestMessage
			{
				Instructions = message.Instructions,
				Input = message.Input?.Clone(onError: exception => throw exception),
				References = message.References?.Select(reference => reference == null ? null : new Reference
				{
					URI = reference.URI,
					Title = reference.Title,
					Description = reference.Description,
					Options = reference.Options?.Clone(true)
				}).ToList(),
				Capabilities = message.Capabilities == null ? new List<string>() : new List<string>(message.Capabilities),
				Schema = schema?.Clone(onError: exception => throw exception),
				Options = options.Clone(onError: exception => throw exception)
			};

		/// <summary>
		/// Reads an execution hint from the existing request extras without modifying them
		/// </summary>
		/// <param name="requestInfo">The business request containing optional extras</param>
		/// <param name="name">The hint key to match without regard to case</param>
		/// <returns>The first matching value, or null when no matching extra is present</returns>
		static string GetHint(RequestInfo requestInfo, string name)
			=> requestInfo.Extra?.FirstOrDefault(pair => name.IsEquals(pair.Key)).Value;
		#endregion

		#region Provider management
		/// <summary>
		/// Adds or replaces a provider profile after initializing and validating a new instance
		/// </summary>
		/// <param name="info">The configuration to copy and publish under its case-insensitive profile name</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>A task completing after publication and any immediately eligible old-provider cleanup</returns>
		/// <remarks>Existing executions retain their selected provider. Cancellation is checked before publication; committed replacement is not rolled back by cleanup.</remarks>
		/// <exception cref="ArgumentNullException">The configuration is null</exception>
		/// <exception cref="ProviderException">The provider configuration is invalid</exception>
		public static async Task UpdateProviderAsync(this ProviderInfo info, CancellationToken cancellationToken = default)
		{
			if (info == null)
				throw new ArgumentNullException(nameof(info));
			var registry = await GetRegistryAsync(cancellationToken).ConfigureAwait(false);
			await registry.UpdateAsync(info, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Removes a provider profile from future resolution without interrupting existing executions
		/// </summary>
		/// <param name="name">The case-insensitive profile name to remove</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>true if a profile was removed; otherwise false</returns>
		/// <remarks>Removing the default profile does not select a fallback. Cleanup of a provider still in use is deferred until its final lease is released.</remarks>
		/// <exception cref="ArgumentException">The profile name is empty</exception>
		public static async Task<bool> RemoveProviderAsync(string name, CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("A provider profile name is required", nameof(name));
			var registry = await GetRegistryAsync(cancellationToken).ConfigureAwait(false);
			return await registry.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
		}

		/// <summary>
		/// Selects an existing profile as the registry default, or clears the default
		/// </summary>
		/// <param name="name">The case-insensitive profile name; null or blank clears the default</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>A task completing after the default selection is updated</returns>
		/// <exception cref="ProviderException">The named profile is not configured</exception>
		public static async Task SetDefaultProviderAsync(string name, CancellationToken cancellationToken = default)
		{
			var registry = await GetRegistryAsync(cancellationToken).ConfigureAwait(false);
			registry.SetDefault(name, cancellationToken);
		}
		#endregion

		#region Execution and retry
		/// <summary>
		/// Coordinates provider selection, request preparation, validation, retries and logical observability
		/// </summary>
		/// <param name="requestInfo">The business context used for selection hints and observability</param>
		/// <param name="message">The original request to prepare without mutation</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>The normalized response with aggregated attempt usage</returns>
		/// <remarks>One provider lease spans every attempt and backoff. Caller cancellation takes precedence over both timeout layers.</remarks>
		static async Task<ResponseMessage> ExecuteLogicalAsync(RequestInfo requestInfo, RequestMessage message, CancellationToken cancellationToken)
		{
			var stopwatch = Stopwatch.StartNew();
			var trace = ServiceExtensions.StartTrace(GetHint(requestInfo, "x-parent-trace-id"), requestInfo.CorrelationID,
				ServiceBase.ServiceComponent?.NodeID ?? ServiceExtensions.GetNodeID(), requestInfo.ServiceName, "AI.Execute");
			UsageInfo usage = null;
			ProviderRegistry.Lease lease = null;
			var status = "error";
			using (var logical = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
				try
				{
					usage = CreateLogicalUsage(requestInfo, trace);
					// Explicit caller/request deadlines can cover the first lazy registry load as well.
					var initialTimeout = message.Options?.Properties().LastOrDefault(property => property.Name.IsEquals("timeout"))?.Value;
					var timeoutHint = GetHint(requestInfo, "x-ai-timeout");
					if (!string.IsNullOrWhiteSpace(timeoutHint))
						initialTimeout = new JValue(timeoutHint);
					SetLogicalTimeout(logical, GetTimeoutMilliseconds(initialTimeout, "timeout", 0), stopwatch);
					var registry = await GetRegistryAsync(logical.Token).ConfigureAwait(false);
					CheckExecutionCancellation(cancellationToken, logical.Token);
					lease = registry.Acquire(GetHint(requestInfo, "x-ai-provider"));
					var provider = lease.Provider;
					var effective = Prepare(message, provider.Info, requestInfo);
					usage.Provider = provider.Info.Name;
					usage.Model = effective.Options["model"]?.ToString();
					trace.Attributes["provider"] = usage.Provider;
					trace.Attributes["model"] = usage.Model;
					// Deduct time already spent preparing the execution from the selected profile's deadline.
					SetLogicalTimeout(logical, GetTimeoutMilliseconds(effective.Options["timeout"], "timeout", 0), stopwatch);
					var attemptTimeout = GetTimeoutMilliseconds(effective.Options["request-timeout"], "request-timeout", 30);
					CheckExecutionCancellation(cancellationToken, logical.Token);
					if (effective.Schema != null && !provider.Supports("structured-output"))
						throw new UnsupportedCapabilityException("The selected AI provider does not support structured output");
					if (effective.Capabilities.Count > 0 && !provider.Supports("remote-mcp"))
						throw new UnsupportedCapabilityException("The selected AI provider does not support remote MCP");
					var validate = effective.Schema.CreateSchemaValidator();
					CheckExecutionCancellation(cancellationToken, logical.Token);
					await PrepareMcpAsync(requestInfo, effective, logical.Token).ConfigureAwait(false);
					CheckExecutionCancellation(cancellationToken, logical.Token);

					// Approved policy: transient retries also apply when mutating MCP capabilities are exposed.
					// Business services own mutation idempotency; a failed attempt may already have committed tool effects.
					for (var attempt = 1; ; attempt++)
					{
						try
						{
							var response = await ExecuteAttemptAsync(provider, effective, validate, trace, usage, attempt,
								attemptTimeout, cancellationToken, logical.Token).ConfigureAwait(false);
							CheckExecutionCancellation(cancellationToken, logical.Token);
							status = "completed".IsEquals(response.Status, StringComparison.Ordinal) || "ok".IsEquals(response.Status, StringComparison.Ordinal)
								? "ok" : response.Status;
							response.Usage = usage;
							return response;
						}
						catch (ProviderException exception) when (exception.IsTransient && attempt < 3)
						{
							CheckExecutionCancellation(cancellationToken, logical.Token);
							var delay = exception.RetryAfter ?? TimeSpan.FromMilliseconds(UtilityService.GetRandomNumber(250, 750) * attempt);
							if (delay < TimeSpan.Zero)
								delay = TimeSpan.Zero;
							await DelayRetryAsync(delay, cancellationToken, logical.Token).ConfigureAwait(false);
						}
					}
				}
				catch (Exception exception)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						status = "canceled";
						var canceled = exception as OperationCanceledException;
						if (canceled != null && canceled.CancellationToken == cancellationToken)
						{
							canceled.Data["Usage"] = usage;
							throw;
						}
						throw new OperationCanceledException(cancellationToken) { Data = { ["Usage"] = usage } };
					}
					if (logical.IsCancellationRequested)
					{
						status = "timeout";
						trace.Attributes["error_code"] = "execution-timeout";
						throw new ProviderException("AI logical execution timed out") { Code = "execution-timeout", Data = { ["Usage"] = usage } };
					}
					trace.Attributes["error_code"] = (exception as ProviderException)?.Code ?? exception.GetType().Name;
					exception.Data["Usage"] = usage;
					WriteExecutionLog(trace, "AI execution failed", exception, usage);
					throw;
				}
				finally
				{
					try { CompleteExecutionLogs(trace, usage, status); }
					finally
					{
						if (lease != null)
							try { await lease.DisposeAsync().ConfigureAwait(false); }
							catch (Exception exception) { WriteExecutionLog(trace, "Cannot release the AI provider lease", exception, usage); }
					}
				}
		}

		/// <summary>
		/// Executes and validates one provider attempt under its own linked timeout
		/// </summary>
		/// <param name="provider">The provider pinned for this logical execution</param>
		/// <param name="message">The prepared request whose canonical values are copied for this attempt</param>
		/// <param name="validate">The canonical output validator prepared before the retry loop</param>
		/// <param name="parent">The logical execution trace</param>
		/// <param name="total">The logical usage record receiving this attempt snapshot</param>
		/// <param name="attempt">The one-based provider attempt number</param>
		/// <param name="timeout">The provider-attempt timeout in milliseconds</param>
		/// <param name="caller">The caller token, which takes precedence over execution deadlines</param>
		/// <param name="logical">The token for the logical execution deadline and linked caller cancellation</param>
		/// <returns>The provider response after canonical validation</returns>
		/// <remarks>Attempt usage and trace completion are recorded on success and failure. Recoverable attempt timeout is normalized for the logical retry loop.</remarks>
		static async Task<ResponseMessage> ExecuteAttemptAsync(Provider provider, RequestMessage message, Action<JToken> validate,
			TraceInfo parent, UsageInfo total, int attempt, int timeout, CancellationToken caller, CancellationToken logical)
		{
			CheckExecutionCancellation(caller, logical);
			var trace = ServiceExtensions.StartTrace(parent.ID, parent.CorrelationID, parent.NodeID, parent.ServiceName, "AI.Provider", "client",
				new Dictionary<string, object> { ["provider"] = total.Provider, ["model"] = total.Model, ["attempt"] = attempt });
			UsageInfo usage = null;
			var status = "error";
			using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(logical))
				try
				{
					timeoutSource.CancelAfter(timeout);
					var effective = CopyMessage(message, message.Schema, message.Options);
					var response = await provider.ExecuteAsync(effective, timeoutSource.Token).ConfigureAwait(false);
					if (response == null)
						throw new ProviderException("AI provider returned no response") { Code = "invalid-response" };
					usage = response.Usage;
					CheckExecutionCancellation(caller, logical);
					if (timeoutSource.IsCancellationRequested)
						throw new OperationCanceledException(timeoutSource.Token);
					if (string.IsNullOrWhiteSpace(response.Status))
						throw new ProviderException("AI provider returned no response status") { Code = "invalid-response" };
					// Validation failure is not a recoverable provider error and is never retried here.
					validate(response.Output);
					CheckExecutionCancellation(caller, logical);
					if (timeoutSource.IsCancellationRequested)
						throw new OperationCanceledException(timeoutSource.Token);
					status = "completed".IsEquals(response.Status, StringComparison.Ordinal) || "ok".IsEquals(response.Status, StringComparison.Ordinal)
						? "ok" : response.Status;
					return response;
				}
				catch (Exception exception)
				{
					usage = usage ?? exception.Data["Usage"] as UsageInfo;
					if (caller.IsCancellationRequested)
					{
						status = "canceled";
						throw;
					}
					if (logical.IsCancellationRequested)
					{
						status = "timeout";
						trace.Attributes["error_code"] = "execution-timeout";
						throw new ProviderException("AI logical execution timed out") { Code = "execution-timeout" };
					}
					if (exception is OperationCanceledException && timeoutSource.IsCancellationRequested)
					{
						status = "timeout";
						trace.Attributes["error_code"] = "request-timeout";
						throw new ProviderException("AI provider attempt timed out") { Code = "request-timeout", IsTransient = true };
					}
					if (exception is ProviderException failure && "request-timeout".IsEquals(failure.Code, StringComparison.Ordinal))
						status = "timeout";
					trace.Attributes["error_code"] = (exception as ProviderException)?.Code ?? exception.GetType().Name;
					throw;
				}
				finally
				{
					try { AggregateUsage(total, usage, trace, attempt, status); }
					finally { CompleteTrace(trace, status, total); }
				}
		}

		/// <summary>
		/// Reads a positive integer timeout expressed in seconds and converts it to milliseconds
		/// </summary>
		/// <param name="value">The integer or string option; a missing value selects the default</param>
		/// <param name="name">The option name used in configuration errors</param>
		/// <param name="defaultSeconds">The fallback in seconds; zero represents no additional logical deadline</param>
		/// <returns>The timeout in milliseconds</returns>
		/// <exception cref="ProviderException">The supplied value is invalid or exceeds the supported timer range</exception>
		static int GetTimeoutMilliseconds(JToken value, string name, int defaultSeconds)
		{
			if (value == null)
				return defaultSeconds * 1000;
			if ((value.Type != JTokenType.Integer && value.Type != JTokenType.String)
				|| !Int32.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
				|| seconds <= 0 || seconds > Int32.MaxValue / 1000)
				throw new ProviderException($"{name} must be a positive number of seconds no greater than {Int32.MaxValue / 1000}") { Code = "invalid-request" };
			return seconds * 1000;
		}

		/// <summary>
		/// Schedules the remaining logical deadline after accounting for elapsed execution time
		/// </summary>
		/// <param name="source">The linked cancellation source for the logical execution</param>
		/// <param name="milliseconds">The total budget in milliseconds; a nonpositive value leaves the source unchanged</param>
		/// <param name="stopwatch">The clock started at logical execution entry</param>
		/// <remarks>An exhausted budget cancels immediately. A source that is already canceled is not rescheduled.</remarks>
		static void SetLogicalTimeout(CancellationTokenSource source, int milliseconds, Stopwatch stopwatch)
		{
			if (milliseconds <= 0 || source.IsCancellationRequested)
				return;
			var remaining = milliseconds - stopwatch.Elapsed.TotalMilliseconds;
			if (remaining <= 0)
				source.Cancel();
			else
				source.CancelAfter(Math.Max(1, (int)remaining));
		}

		/// <summary>
		/// Checks caller cancellation before the logical execution deadline
		/// </summary>
		/// <param name="caller">The caller token, which takes precedence over execution deadlines</param>
		/// <param name="logical">The token for the logical execution deadline and linked caller cancellation</param>
		/// <exception cref="OperationCanceledException">The caller has canceled</exception>
		/// <exception cref="ProviderException">The logical execution deadline has expired</exception>
		static void CheckExecutionCancellation(CancellationToken caller, CancellationToken logical)
		{
			caller.ThrowIfCancellationRequested();
			if (logical.IsCancellationRequested)
				throw new ProviderException("AI logical execution timed out") { Code = "execution-timeout" };
		}

		/// <summary>
		/// Waits for the retry delay while honoring caller cancellation and the logical deadline
		/// </summary>
		/// <param name="delay">The provider Retry-After interval or generated jitter delay</param>
		/// <param name="caller">The caller token, which takes precedence over execution deadlines</param>
		/// <param name="logical">The token for the logical execution deadline and linked caller cancellation</param>
		/// <returns>A task completing after the requested delay</returns>
		/// <remarks>Long delays are divided into timer-compatible chunks without shortening the requested interval.</remarks>
		static async Task DelayRetryAsync(TimeSpan delay, CancellationToken caller, CancellationToken logical)
		{
			// Chunk large provider Retry-After values without shortening them to the Task.Delay limit.
			var stopwatch = Stopwatch.StartNew();
			while (delay > stopwatch.Elapsed)
			{
				CheckExecutionCancellation(caller, logical);
				var remaining = delay - stopwatch.Elapsed;
				if (remaining <= TimeSpan.Zero)
					break;
				await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, Int32.MaxValue - 1)), logical).ConfigureAwait(false);
			}
			CheckExecutionCancellation(caller, logical);
		}
		#endregion

		#region MCP capability resolution
		/// <summary>
		/// Resolves the explicit MCP scope from the current business service without executing tools
		/// </summary>
		/// <param name="requestInfo">The business request passed to the current service metadata method</param>
		/// <param name="message">The effective request receiving exact tool names and execution-owned MCP metadata</param>
		/// <param name="cancellationToken">The token used to cancel the operation</param>
		/// <returns>A task completing after all requested capability names are resolved</returns>
		/// <remarks>Null or empty scope exposes no tools. Caller-supplied tool metadata is replaced; metadata resolution does not grant business authorization.</remarks>
		/// <exception cref="ProviderException">The scope or requested tool metadata is invalid, or the current service is unavailable</exception>
		/// <exception cref="UnsupportedCapabilityException">A requested capability is absent from the service metadata</exception>
		internal static async Task PrepareMcpAsync(RequestInfo requestInfo, RequestMessage message, CancellationToken cancellationToken)
		{
			// This is execution-owned metadata. Never accept a caller-supplied tool catalog.
			message.Options["mcp-tools"] = new JArray();
			if (message.Capabilities == null || message.Capabilities.Count == 0)
				return;

			if (message.Capabilities.Any(string.IsNullOrWhiteSpace))
				throw new ProviderException("MCP capability names must not be empty") { Code = "invalid-request" };
			var names = message.Capabilities.Select(name => name.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			var service = ServiceBase.ServiceComponent ?? throw new ProviderException("The current business service is unavailable") { Code = "invalid-configuration" };
			cancellationToken.ThrowIfCancellationRequested();
			var settings = await service.GetMcpSettingsAsync(requestInfo, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			var available = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
			if ((settings as JObject)?["Tools"] is JObject tools)
				foreach (var item in tools.Properties())
				{
					if (!(item.Value is JObject group))
						continue;
					// A service override may already supply individual MCP tool definitions.
					if (group["name"]?.Type == JTokenType.String && group["inputSchema"] is JObject)
					{
						AddMcpTool(available, names, group["name"].Value<string>(), group);
						continue;
					}
					if (!(group["schemas"] is JObject schemas) || group["service"]?.Type != JTokenType.String || group["object"]?.Type != JTokenType.String)
						continue;
					foreach (var operation in schemas.Properties())
					{
						var name = $"{group["service"].Value<string>()}.{group["object"].Value<string>()}.{operation.Name}".ToLowerInvariant();
						if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
							continue;
						if (!(operation.Value is JObject definition) || !(definition["input"] is JObject input))
							throw new ProviderException("The requested MCP tool has no input schema") { Code = "invalid-configuration" };
						var tool = new JObject
						{
							["name"] = name,
							["title"] = group["title"]?.Clone(onError: exception => throw exception),
							["description"] = group["description"]?.Clone(onError: exception => throw exception),
							["inputSchema"] = input.Clone(onError: exception => throw exception)
						};
						if (definition["output"] is JObject output)
							tool["outputSchema"] = output.Clone(onError: exception => throw exception);
						AddMcpTool(available, names, name, tool);
					}
				}

			foreach (var name in names)
				if (!available.ContainsKey(name))
					throw new UnsupportedCapabilityException($"The requested MCP capability is unavailable: {name}");
			// Keep the service's exact wire names; provider-side MCP filters can be case-sensitive.
			message.Capabilities = names.Select(name => available[name]["name"].Value<string>()).ToList();
			message.Options["mcp-tools"] = new JArray(names.Select(name => available[name].Clone()));
		}

		/// <summary>
		/// Copies one requested tool definition into the case-insensitive execution catalog
		/// </summary>
		/// <param name="tools">The catalog being assembled</param>
		/// <param name="names">The explicit requested capability names</param>
		/// <param name="name">The exact tool name supplied by service metadata</param>
		/// <param name="tool">The definition to clone when its name is requested</param>
		/// <exception cref="ProviderException">A requested tool name has duplicate definitions</exception>
		static void AddMcpTool(Dictionary<string, JObject> tools, List<string> names, string name, JObject tool)
		{
			if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
				return;
			if (tools.ContainsKey(name))
				throw new ProviderException("The requested MCP capability has duplicate definitions") { Code = "invalid-configuration" };
			tools.Add(name, tool.Clone());
		}
		#endregion

		#region Structured output validation
		/// <summary>
		/// Validates JSON output against the canonical schema without modifying either value
		/// </summary>
		/// <param name="output">The output to validate; a JSON null token is distinct from missing output</param>
		/// <param name="schema">The canonical JSON schema; null allows unstructured output</param>
		/// <exception cref="StructuredOutputException">The output does not satisfy the schema</exception>
		/// <exception cref="ProviderException">The schema is invalid or the validation component fails</exception>
		public static void Validate(this JToken output, JObject schema)
			=> schema.CreateSchemaValidator()(output);

		/// <summary>
		/// Prepares a component-independent canonical output validator before provider attempts
		/// </summary>
		/// <param name="schema">The canonical schema to compile; null produces a no-op validator</param>
		/// <returns>A validator reusable across attempts of the same logical execution</returns>
		/// <remarks>Schema constraints and version are preserved. Internal references resolve; external schema documents are not fetched. Component-specific types remain within this validation region.</remarks>
		/// <exception cref="ProviderException">The schema cannot be prepared</exception>
		/// <exception cref="StructuredOutputException">The returned validator receives missing or schema-invalid output</exception>
		internal static Action<JToken> CreateSchemaValidator(this JObject schema)
		{
			if (schema == null)
				return output => { };

			JSchema compiledSchema;
			try
			{
				// Isolate component parsing from the caller's canonical schema. Do not rewrite its constraints/version.
				using (var reader = schema.Clone(onError: exception => throw exception).CreateReader())
					compiledSchema = JSchema.Load(reader, new JSchemaReaderSettings
					{
						ValidateVersion = true,
						ResolveSchemaReferences = true,
						// An empty preloaded resolver has no URL/file fallback. Internal references still resolve.
						Resolver = new JSchemaPreloadedResolver()
					});
			}
			catch (JSchemaReaderException)
			{
				throw new ProviderException("The canonical JSON schema is invalid or contains unresolved references") { Code = "invalid-schema" };
			}
			catch (JSchemaException)
			{
				throw new ProviderException("The JSON schema validation component could not prepare the schema") { Code = "schema-validation-error" };
			}
			catch (JsonException)
			{
				throw new ProviderException("The canonical JSON schema could not be read") { Code = "invalid-schema" };
			}

			return output =>
			{
				if (output == null)
					throw new StructuredOutputException("AI output is missing while a structured output schema is required");
				try
				{
					if (SchemaExtensions.IsValid(output, compiledSchema, out IList<ValidationError> errors))
						return;

					var exception = new StructuredOutputException();
					// Keep paths and constraint types only, not output values or component-specific error objects.
					exception.Data["ValidationErrors"] = errors.Take(32).ToJArray(error => new JObject
					{
						["Path"] = error.Path,
						["Type"] = error.ErrorType.ToString()
					});
					exception.Data["ValidationErrorCount"] = errors.Count;
					throw exception;
				}
				catch (JSchemaException)
				{
					throw new ProviderException("The JSON schema validation component failed") { Code = "schema-validation-error" };
				}
				catch (JsonException)
				{
					throw new StructuredOutputException("AI output could not be read for schema validation");
				}
			};
		}
		#endregion

		#region Usage and diagnostics
		/// <summary>
		/// Starts the existing usage contract for one logical AI execution with empty attempt totals
		/// </summary>
		/// <param name="requestInfo">The business context used to enrich the usage record</param>
		/// <param name="trace">The logical execution trace associated with the usage</param>
		/// <returns>The logical usage record with an attempt snapshot collection</returns>
		static UsageInfo CreateLogicalUsage(RequestInfo requestInfo, TraceInfo trace)
		{
			var usage = ServiceExtensions.StartUsage(requestInfo, "AI.Execute", "ai", traceID: trace.ID);
			usage.Attempt = 0;
			usage.ModelRequests = 0;
			usage.ToolCalls = 0;
			usage.MCPCalls = 0;
			usage.InputTokens = 0;
			usage.CachedInputTokens = 0;
			usage.CacheCreationInputTokens = 0;
			usage.OutputTokens = 0;
			usage.ReasoningTokens = 0;
			usage.TotalTokens = 0;
			usage.InputBytes = 0;
			usage.OutputBytes = 0;
			usage.ProviderCost = 0;
			usage.Details["attempts"] = new List<UsageInfo>();
			return usage;
		}

		/// <summary>
		/// Adds one provider attempt snapshot and its available counters to logical usage
		/// </summary>
		/// <param name="total">The logical usage record to update</param>
		/// <param name="usage">The provider usage, or null when the attempt did not supply usage</param>
		/// <param name="trace">The provider-attempt trace supplying context and fallback timing</param>
		/// <param name="attempt">The one-based provider attempt number</param>
		/// <param name="status">The normalized outcome of this attempt</param>
		/// <remarks>Unknown counters remain unknown. Cached and reasoning subsets are not added again to token totals. Costs are summed only when amounts and currencies permit aggregation.</remarks>
		/// <exception cref="ProviderException">Summing the supplied counters or costs overflows</exception>
		static void AggregateUsage(UsageInfo total, UsageInfo usage, TraceInfo trace, int attempt, string status)
		{
			// Copy the existing usage contract, excluding arbitrary provider diagnostics/raw data.
			var snapshot = usage?.Copy(new HashSet<string> { nameof(UsageInfo.Details) }, onError: exception => throw exception) ?? new UsageInfo();
			snapshot.ID = snapshot.ID ?? UtilityService.NewUUID;
			snapshot.CorrelationID = total.CorrelationID;
			snapshot.TraceID = trace.ID;
			snapshot.DeveloperID = total.DeveloperID;
			snapshot.AppID = total.AppID;
			snapshot.UserID = total.UserID;
			snapshot.NodeID = total.NodeID;
			snapshot.ServiceName = total.ServiceName;
			snapshot.ObjectName = total.ObjectName;
			snapshot.SystemID = total.SystemID;
			snapshot.Type = total.Type;
			snapshot.Operation = total.Operation;
			snapshot.Provider = total.Provider;
			snapshot.Model = snapshot.Model ?? total.Model;
			snapshot.Attempt = attempt;
			snapshot.Status = status;
			if (snapshot.StartTime == default)
				snapshot.StartTime = trace.StartTime;
			snapshot.EndTime = snapshot.EndTime ?? (snapshot.StartTime.Kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now);
			snapshot.DurationMs = snapshot.DurationMs ?? (snapshot.EndTime.Value - snapshot.StartTime).TotalMilliseconds;
			((List<UsageInfo>)total.Details["attempts"]).Add(snapshot);
			total.Attempt = attempt;
			if (usage == null)
				total.Details["incomplete_usage"] = true;

			// A missing counter stays unknown, including when other attempts supplied that counter.
			try
			{
				total.ModelRequests = AddUsage(total.ModelRequests, snapshot.ModelRequests);
				total.ToolCalls = AddUsage(total.ToolCalls, snapshot.ToolCalls);
				total.MCPCalls = AddUsage(total.MCPCalls, snapshot.MCPCalls);
				total.InputTokens = AddUsage(total.InputTokens, snapshot.InputTokens);
				total.CachedInputTokens = AddUsage(total.CachedInputTokens, snapshot.CachedInputTokens);
				total.CacheCreationInputTokens = AddUsage(total.CacheCreationInputTokens, snapshot.CacheCreationInputTokens);
				total.OutputTokens = AddUsage(total.OutputTokens, snapshot.OutputTokens);
				total.ReasoningTokens = AddUsage(total.ReasoningTokens, snapshot.ReasoningTokens);
				total.TotalTokens = AddUsage(total.TotalTokens, snapshot.TotalTokens);
				total.InputBytes = AddUsage(total.InputBytes, snapshot.InputBytes);
				total.OutputBytes = AddUsage(total.OutputBytes, snapshot.OutputBytes);

				if (attempt == 1)
				{
					total.ProviderCurrency = snapshot.ProviderCurrency;
					total.PricingVersion = snapshot.PricingVersion;
				}
				else
				{
					if (!(total.ProviderCurrency ?? "").IsEquals(snapshot.ProviderCurrency ?? ""))
					{
						total.Details[!string.IsNullOrWhiteSpace(total.ProviderCurrency) && !string.IsNullOrWhiteSpace(snapshot.ProviderCurrency)
							? "mixed_currency" : "incomplete_cost"] = true;
						total.ProviderCurrency = null;
						total.ProviderCost = null;
					}
					if (!(total.PricingVersion ?? "").IsEquals(snapshot.PricingVersion ?? "", StringComparison.Ordinal))
						total.PricingVersion = null;
				}
				total.ProviderCost = !string.IsNullOrWhiteSpace(total.ProviderCurrency) && total.ProviderCost.HasValue && snapshot.ProviderCost.HasValue
					? checked(total.ProviderCost.Value + snapshot.ProviderCost.Value) : (decimal?)null;
			}
			catch (OverflowException)
			{
				throw new ProviderException("Aggregated AI usage counters overflowed") { Code = "invalid-usage" };
			}
		}

		/// <summary>
		/// Adds two known usage counters while preserving unknown values
		/// </summary>
		/// <param name="total">The current total, or null when unknown</param>
		/// <param name="value">The attempt value, or null when unknown</param>
		/// <returns>The checked sum when both values are known; otherwise null</returns>
		/// <exception cref="OverflowException">The sum exceeds the counter type range</exception>
		static int? AddUsage(int? total, int? value)
			=> total.HasValue && value.HasValue ? checked(total.Value + value.Value) : (int?)null;

		/// <summary>
		/// Adds two known usage counters while preserving unknown values
		/// </summary>
		/// <param name="total">The current total, or null when unknown</param>
		/// <param name="value">The attempt value, or null when unknown</param>
		/// <returns>The checked sum when both values are known; otherwise null</returns>
		/// <exception cref="OverflowException">The sum exceeds the counter type range</exception>
		static long? AddUsage(long? total, long? value)
			=> total.HasValue && value.HasValue ? checked(total.Value + value.Value) : (long?)null;

		/// <summary>
		/// Completes logical usage and its trace through existing observability helpers
		/// </summary>
		/// <param name="trace">The logical execution trace</param>
		/// <param name="usage">The logical usage record, or null when initialization failed</param>
		/// <param name="status">The final normalized execution outcome</param>
		/// <remarks>Completion submits observability records; it is not a persistence acknowledgement. Completion errors are diagnosed without replacing the execution outcome.</remarks>
		static void CompleteExecutionLogs(TraceInfo trace, UsageInfo usage, string status)
		{
			if (usage != null)
				try { ServiceExtensions.Complete(usage, status); }
				catch (Exception exception) { WriteExecutionLog(trace, "Cannot complete AI usage tracking", exception, usage); }
			CompleteTrace(trace, status, usage);
		}

		/// <summary>
		/// Completes an existing trace and diagnoses completion failures
		/// </summary>
		/// <param name="trace">The logical or provider-attempt trace</param>
		/// <param name="status">The normalized outcome to record</param>
		/// <param name="context">The usage context for diagnostic logging</param>
		static void CompleteTrace(TraceInfo trace, string status, UsageInfo context)
		{
			try { ServiceExtensions.Complete(trace, status); }
			catch (Exception exception) { WriteExecutionLog(trace, "Cannot complete AI trace tracking", exception, context); }
		}

		/// <summary>
		/// Writes execution diagnostics through existing ServiceLog infrastructure
		/// </summary>
		/// <param name="trace">The trace supplying correlation and service context</param>
		/// <param name="message">The diagnostic description</param>
		/// <param name="exception">The failure to record</param>
		/// <param name="context">The optional usage record supplying business context</param>
		/// <remarks>Logging failures are suppressed so diagnostics do not replace the execution result or original error.</remarks>
		static void WriteExecutionLog(TraceInfo trace, string message, Exception exception, UsageInfo context)
		{
			try
			{
				ServiceExtensions.WriteServiceLog(trace.CorrelationID, context?.DeveloperID, context?.AppID, ServiceBase.ServiceComponent?.Logger,
					new List<string> { message }, exception, trace.ServiceName, context?.ObjectName);
			}
			catch
			{
				// Logging failures must not replace the execution result or its original error.
			}
		}
		#endregion
	}
}
