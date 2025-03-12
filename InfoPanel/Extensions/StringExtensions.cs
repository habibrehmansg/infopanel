using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static class StringExtensions
    {
        public static bool IsUrl(this string value)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uriResult))
            {
                return uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps;
            }
            return false;
        }
    }
}
