#region Related components
using System;
using System.Reflection;

using WampSharp.V2;
using WampSharp.V2.Core.Contracts;
#endregion

namespace net.vieapps.Services
{
	/// <summary>
	/// Presents a proxy interceptor for calling a service
	/// </summary>
	public class ServiceInterceptor : CalleeProxyInterceptor
	{
		string _name;

		public ServiceInterceptor(string name) : base(new CallOptions())
		{
			this._name = name;
		}

		public override string GetProcedureUri(MethodInfo method)
		{
			return string.Format(base.GetProcedureUri(method), this._name);
		}
	}
}