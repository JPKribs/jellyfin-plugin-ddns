using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;

namespace Jellyfin.Plugin.DynamicDns.Utilities;

/// <summary>
/// Finds text XML cannot hold. Jellyfin saves the configuration as XML and writes such a character as an
/// entity it then refuses to read back, so on the next start every setting is replaced with defaults.
/// </summary>
public static class XmlText
{
    /// <summary>
    /// The first property, anywhere in the object, holding a character XML cannot store.
    /// </summary>
    /// <param name="value">The configuration or part of it.</param>
    /// <returns>The property's path, such as <c>Records[0].Hostname</c>, or null when every value is safe.</returns>
    public static string? FindInvalid(object? value) => Find(value, string.Empty, 0);

    private static string? Find(object? value, string path, int depth)
    {
        if (value is null || depth > 16)
        {
            return null;
        }

        if (value is string text)
        {
            return IsValid(text) ? null : path;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is DateTime or Guid or decimal or TimeSpan)
        {
            return null;
        }

        if (value is IEnumerable list)
        {
            var index = 0;
            foreach (var item in list)
            {
                var found = Find(item, path + "[" + index + "]", depth + 1);
                if (found is not null)
                {
                    return found;
                }

                index++;
            }

            return null;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            var found = Find(property.GetValue(value), path.Length == 0 ? property.Name : path + "." + property.Name, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsValid(string text)
    {
        try
        {
            XmlConvert.VerifyXmlChars(text);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
