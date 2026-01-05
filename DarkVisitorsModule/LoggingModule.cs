using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Web;

namespace DarkVisitors
{
	public record Request(string request_path, string request_method, Dictionary<string, string> request_headers);

	public class DisallowedUserAgentException : HttpException 
	{
		public DisallowedUserAgentException() : base(404, "Not Found") { }
	}

	public sealed class LoggingModule : IHttpModule
	{
		readonly string _endpoint = "https://api.darkvisitors.com/visits";
		readonly string _token = "paste token here";
		readonly HttpClient _client = new();
		
		// Don't report anything in these paths to DV; we don't really care about AI scraping them
		// (they're all available elsewhere), and we don't want a zillion outgoing DV requests
		// when humans visit the site for these. 
		private static readonly string[] _ignoreRoots = { "/vendor", "/js", "/scss" };

		private static readonly string[] _knownMiscreants = { 
			"ClaudeBot",
			"Scrapy",
			"GoogleOther",
			"Timpibot",
			"Nutch",
			"HTTrack",
			"Dataprovider.com",
			"Bytespider",
			"Diffbot"
		};

		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}

		public void Init(HttpApplication context)
		{
			_client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_token}");
			context.BeginRequest += BeginRequest;
			context.LogRequest += LogRequest;
		}

		private void BeginRequest(object sender, EventArgs e)
		{
			HttpContext context = ((HttpApplication)sender).Context;
			var agent = context.Request.Headers["User-Agent"];

			if(ShouldSendToTheVoid(agent))
			{
				throw new DisallowedUserAgentException();
			}
		}

		private void LogRequest(object sender, EventArgs e)
		{
			HttpContext context = ((HttpApplication)sender).Context;

			if(context.Error is DisallowedUserAgentException)
			{
				return;
			}

			var path = context.Request.Path;
			var headers = context.Request.Headers;
			
			if (ShouldIgnore(path, headers))
			{
				return;
			}

			var request = new Request(path, context.Request.HttpMethod,
				headers.AllKeys.ToDictionary(k => k, k => headers[k]));

			var requestString = JsonConvert.SerializeObject(request);
			var content = new StringContent(requestString, Encoding.UTF8, "application/json");

			_client.PostAsync(_endpoint, content);
		}

		bool ShouldSendToTheVoid(string agent)
		{
			if(agent == null)
			{
				return false;
			}

			return _knownMiscreants.Any(m => agent.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
		}

		bool ShouldIgnore(string path, NameValueCollection headers)
		{
			if (path.EndsWith(".html", true, CultureInfo.InvariantCulture))
			{
				// .html requests will get redirected to the URL without the path, so don't bother 
				// reporting those to Dark Visitors; we'll report the rewritten request instead
				return true;
			}

			foreach (var root in _ignoreRoots)
			{
				if (path.StartsWith(root, true, CultureInfo.InvariantCulture))
				{
					return true;
				}
			}

			return IsPrefetch(headers) || IsAppInsightsPing(headers);
		}

		// Determines if request is a site availability ping from Azure App Insights
		bool IsAppInsightsPing(NameValueCollection headers)
		{
			var userAgent = headers["User-Agent"];
			return userAgent != null && userAgent.Contains("AppInsights");
		}

		// Prefetching by QuickLinks
		bool IsPrefetch(NameValueCollection headers)
		{
			var purpose = headers["Sec-Purpose"];
			return purpose != null && purpose == "prefetch";
		}
	}
}