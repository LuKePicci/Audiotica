﻿#region

using System.Threading.Tasks;
using Newtonsoft.Json;

#endregion

namespace Audiotica.Core.Utils
{
    public static class StringExtensions
    {
        public static string CleanForFileName(this string str)
        {
            /*
             * A filename cannot contain any of the following characters:
             * \ / : * ? " < > |
             */
            return str
                .Replace("\\", "")
                .Replace("/", "")
                .Replace(":", " ")
                .Replace("*", "")
                .Replace("?", "")
                .Replace("\"", "'")
                .Replace("<", "")
                .Replace(">", "")
                .Replace("|", " ");
        }

        public static string StripHtmlTags(this string str)
        {
            return HtmlRemoval.StripTagsRegex(str);
        }

        public static async Task<T> DeserializeAsync<T>(this string json)
        {
            return await Task.Factory.StartNew(() =>
            {
                try
                {
                    return JsonConvert.DeserializeObject<T>(json);
                }
                catch
                {
                    return default(T);
                }
            }).ConfigureAwait(false);
        }
    }
}