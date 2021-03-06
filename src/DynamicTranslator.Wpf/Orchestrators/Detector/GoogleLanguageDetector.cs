﻿using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web;

using Abp.Dependency;

using DynamicTranslator.Configuration;

using Newtonsoft.Json;

using RestSharp;

namespace DynamicTranslator.Wpf.Orchestrators.Detector
{
    public class GoogleLanguageDetector : ILanguageDetector, ISingletonDependency
    {
        private readonly IDynamicTranslatorStartupConfiguration configuration;

        public GoogleLanguageDetector(IDynamicTranslatorStartupConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public async Task<string> DetectLanguage(string text)
        {
            var uri = string.Format(
                configuration.GoogleTranslateUrl,
                configuration.ToLanguageExtension,
                configuration.ToLanguageExtension,
                HttpUtility.UrlEncode(text));

            var response = await new RestClient(uri)
                .ExecuteGetTaskAsync(new RestRequest(Method.GET)
                    .AddHeader("Accept-Language", "en-US,en;q=0.8,tr;q=0.6")
                    .AddHeader("Accept-Encoding", "gzip, deflate, sdch")
                    .AddHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/46.0.2490.80 Safari/537.36")
                    .AddHeader("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8"));

            var result = await Task.Run(() => JsonConvert.DeserializeObject<Dictionary<string, object>>(response.Content));
            return result?["src"]?.ToString() ?? configuration.FromLanguageExtension;
        }
    }
}