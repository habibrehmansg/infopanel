using System.Text;

namespace InfoPanel.Utils
{
    public static class StringUtils
    {
        /// <summary>
        /// Sanitizes a string by removing null characters and other invalid XML characters.
        /// Also removes any non-printable characters.
        /// </summary>
        /// <param name="input">The string to sanitize</param>
        /// <returns>A sanitized string safe for XML serialization</returns>
        public static string SanitizeString(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            var sanitized = new StringBuilder();
            foreach (char c in input)
            {
                if (c != '\0' && !char.IsControl(c))
                {
                    sanitized.Append(c);
                }
            }
            
            return sanitized.ToString().Trim();
        }
    }
}