using System;
using System.Text;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>
/// The gadget source's keys: <c>plugin:/infopanel.hwinfo-gadget/{sensor}/{label}</c>, by HWiNFO's names, never its
/// index (which renumbers whenever a reading is ticked or unticked). Each name is HWiNFO's, trimmed, with <c>%</c>,
/// <c>/</c> and <c>~</c> escaped (<c>%25</c>, <c>%2F</c>, <c>%7E</c>; <c>~</c> stays free for a later discriminator).
/// Readings that share a sensor and label get no key (<see cref="HwInfoGadgetSampler"/> skips them): an ordinal
/// suffix would move to another reading whenever one is unticked.
/// </summary>
public static class GadgetKeys
{
    /// <summary>A sensor or label name longer than this (trimmed) skips its reading.</summary>
    public const int MaxNameLength = 255;

    /// <summary>A key (its whole text, <c>plugin:/…</c>) longer than this skips its reading: keys are at most 512 characters.</summary>
    public const int MaxKeyLength = 512;

    /// <summary><paramref name="name"/> with <c>%</c>, <c>/</c> and <c>~</c> escaped.</summary>
    public static string Escape(string name)
    {
        if (name.AsSpan().IndexOfAny('%', '/', '~') < 0)
            return name;

        var b = new StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            b.Append(c switch
            {
                '%' => "%25",
                '/' => "%2F",
                '~' => "%7E",
                _ => null,
            } ?? c.ToString());
        }

        return b.ToString();
    }

    /// <summary>The definition id of the reading named <paramref name="sensor"/> and <paramref name="label"/> (trimmed names): <c>{sensor}/{label}</c>.</summary>
    public static string IdOf(string sensor, string label) => Escape(sensor) + "/" + Escape(label);

    /// <summary>Why a trimmed name can't be part of a key (empty, a control character, too long), or null when it can.</summary>
    public static string? ProblemOf(string name)
    {
        if (name.Length == 0)
            return "an empty name";
        if (name.Length > MaxNameLength)
            return $"a name longer than {MaxNameLength} characters";
        foreach (var c in name)
        {
            if (char.IsControl(c))
                return "a control character in its name";
        }

        return null;
    }
}
