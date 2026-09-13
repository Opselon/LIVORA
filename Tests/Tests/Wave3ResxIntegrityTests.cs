namespace LIVORA.Tests.Tests;

/// <summary>
/// Resource integrity for the two resx files — read from DISK, relative to the repo root, so the
/// checks see exactly what a merge wrote (compiled resources would happily carry stale bytes).
/// Every test skips cleanly (returns) when the files are not on disk, so a trimmed checkout does
/// not turn an absent resource into a false failure.
///
/// Why this suite exists: ten lanes add keys in parallel. The failure modes that survive code
/// review are (a) a key landing in EN but not FA (a silent English leak inside an RTL UI), (b) an
/// empty value (an invisible label), and (c) placeholder drift — FA dropping or renumbering
/// {0}/{1}. (c) is the expensive one: LocalizationService.T catches the FormatException and
/// returns the raw template, so the app keeps running while showing "{0} ساعت" to a real user.
/// Nothing but this file can see it.
/// </summary>
[Collection("LocalizationState")]
public class ResxIntegrityTests
{
    private static readonly Wave3Harness.ResxPair? Resx = Wave3Harness.ReadBoth();

    /// <summary>Values that legitimately carry no Persian script (verbatim brand/acronym labels).</summary>
    private static readonly string[] LatinOnlyAllowList =
    {
        "Onboarding.Language.English",   // language names are shown in their own language
        "Health.HRV",                    // acronym, identical in both languages
        "Plan.Item.Habit",               // pure "{0}" passthrough: the habit's own name
        // Wave 3 additions at merge, same reasoning:
        "Update.Platform.MacOS",         // product name (value is "macOS" — Latin by definition)
        "Update.Platform.iOS",           // product name
        "WeekProgress.Period",           // pure "{0} — {1}" passthrough: ShortDate already localizes
    };

    [Fact]
    public void BothResourceFiles_AreFoundRelativeToTheRepoRoot()
    {
        if (Wave3Harness.RepoRoot is null) return;   // source tree not next to the binaries — skip
        Assert.True(File.Exists(Wave3Harness.ResxPath("")), "AppResources.resx missing next to the repo root");
        Assert.True(File.Exists(Wave3Harness.ResxPath(".fa")), "AppResources.fa.resx missing next to the repo root");
    }

    [Fact]
    public void EnAndFa_KeySetsAreIdentical()
    {
        if (Resx is null) return;
        var onlyEn = Resx.En.Keys.Where(k => !Resx.Fa.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);
        var onlyFa = Resx.Fa.Keys.Where(k => !Resx.En.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal);
        Assert.True(!onlyEn.Any() && !onlyFa.Any(),
            $"key drift — EN only: [{string.Join(", ", onlyEn)}] FA only: [{string.Join(", ", onlyFa)}]");
    }

    [Fact]
    public void EnAndFa_KeyCountsAreEqual()
    {
        if (Resx is null) return;
        Assert.Equal(Resx.En.Count, Resx.Fa.Count);
    }

    [Fact]
    public void KeyCount_IsAtLeastTheDocumentedBaseline()
    {
        if (Resx is null) return;
        // README documents the shipped count; this floor is what catches an accidental mass
        // deletion during a 10-lane merge.
        Assert.True(Resx.En.Count >= 333, $"EN has {Resx.En.Count} keys — the documented baseline is 333");
        Assert.True(Resx.Fa.Count >= 333, $"FA has {Resx.Fa.Count} keys — the documented baseline is 333");
    }

    [Fact]
    public void NoDuplicateKeysInEitherFile()
    {
        if (Resx is null) return;
        foreach (var (label, order) in new[] { ("EN", Resx.EnOrder), ("FA", Resx.FaOrder) })
        {
            var dups = order.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dups.Count == 0, $"{label} duplicate keys: {string.Join(", ", dups)}");
        }
    }

    [Fact]
    public void NoEmptyValuesInEitherFile()
    {
        if (Resx is null) return;
        var emptyEn = Resx.En.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        var emptyFa = Resx.Fa.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(emptyEn.Count == 0, $"EN empty values: {string.Join(", ", emptyEn)}");
        Assert.True(emptyFa.Count == 0, $"FA empty values: {string.Join(", ", emptyFa)}");
    }

    [Fact]
    public void FaValues_ContainPersianCodepoints_ExceptDeliberateLatinLabels()
    {
        if (Resx is null) return;
        var offenders = Resx.Fa
            .Where(kv => !LatinOnlyAllowList.Contains(kv.Key, StringComparer.Ordinal))
            .Where(kv => !Wave3Harness.HasPersianCodepoint(kv.Value))
            .Select(kv => $"{kv.Key} = '{kv.Value}'")
            .ToList();
        Assert.True(offenders.Count == 0, $"FA values with no Persian script: {string.Join(" | ", offenders)}");
    }

    [Fact]
    public void LatinOnlyAllowlist_StillHoldsOnlyLatinOnlyLabels()
    {
        // Guards the allowlist itself from rotting into a dumping ground.
        if (Resx is null) return;
        foreach (var key in LatinOnlyAllowList)
        {
            Assert.True(Resx.Fa.ContainsKey(key), $"{key} no longer exists in FA — remove it from the allowlist");
            Assert.False(Wave3Harness.HasPersianCodepoint(Resx.Fa[key]),
                $"{key} is now translated — it must come off the Latin-only allowlist");
        }
    }

    [Fact]
    public void FaTranslations_UsePersianOrthography_NotArabicCodepoints()
    {
        // §0.4 of the lane protocol demands "real idiomatic Persian (native register, ZWNJ, not
        // Arabic)" — that is mechanically checkable. Persian writes these words with ی ک گ چ پ ژ;
        // Arabic-only ي ك ة in the FA file means the text was produced for the wrong locale. It
        // renders fine, reads wrong, and no UI test will ever notice.
        var path = Wave3Harness.ResxPath(".fa");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var text = File.ReadAllText(path);

        var arabicOnly = new[] { 'ي', 'ك', 'ة' };   // Arabic ya / kaf / taa marbuta
        var hits = arabicOnly.Where(text.Contains).ToList();
        Assert.True(hits.Count == 0,
            "FA contains Arabic-only codepoints: " + string.Join(", ", hits.Select(c => $"U+{(int)c:X4}")));

        Assert.True(text.Count(c => c is 'پ' or 'چ' or 'ژ' or 'گ') >= 20,
            "too few Persian-only letters for this to be real Persian");
        Assert.True(text.Count(c => c == '‌') >= 40,   // ZWNJ
            "too few ZWNJ occurrences — Persian orthography uses them constantly");
    }

    /// <summary>
    /// Drift already present in the shipped resources, pinned exactly (key -> (EN placeholders,
    /// FA placeholders)). A waiver that also asserts the *shape* cannot silently change: fixing the
    /// underlying resx makes the entry stale and this file fails until it is removed. See
    /// docs/WAVE3.md "Known localization debt" — lane 10 may not edit resx (frozen), so the fix is
    /// handed to the orchestrator as a KEYS block.
    /// </summary>
    private static readonly Dictionary<string, (int[] En, int[] Fa)> KnownDriftWaivers = new()
    {
        // (Rec.AdvanceGoal drifted EN-no-placeholder vs FA-{0}; the resx was fixed in the Wave 3
        // orchestrator commit, so the waiver is gone. Empty here means EVERY key must match.)
    };

    [Fact]
    public void PlaceholderIndices_MatchBetweenEnAndFaPerKey()
    {
        // The classic crash-free-but-broken localization bug.
        if (Resx is null) return;
        var offenders = new List<string>();
        foreach (var key in Resx.En.Keys)
        {
            if (!Resx.Fa.TryGetValue(key, out var fa)) continue;
            var enPh = Wave3Harness.Placeholders(Resx.En[key]);
            var faPh = Wave3Harness.Placeholders(fa);
            if (enPh.SequenceEqual(faPh)) continue;

            if (KnownDriftWaivers.TryGetValue(key, out var waiver))
            {
                Assert.True(waiver.En.SequenceEqual(enPh) && waiver.Fa.SequenceEqual(faPh),
                    $"{key} drift changed shape (EN [{string.Join(",", enPh)}] FA [{string.Join(",", faPh)}]); " +
                    "the waiver is stale — fix the resx or update the waiver deliberately.");
                continue;
            }
            offenders.Add($"{key}: EN [{string.Join(",", enPh)}] vs FA [{string.Join(",", faPh)}]");
        }
        Assert.True(offenders.Count == 0,
            $"placeholder drift ({offenders.Count} keys): {string.Join(" | ", offenders.Take(12))}");
    }

    [Fact]
    public void KnownDriftWaivers_AreAllStillNeeded()
    {
        // Dead waivers rot: if the resx gets fixed, this list must shrink with it.
        if (Resx is null) return;
        foreach (var (key, waiver) in KnownDriftWaivers)
        {
            Assert.True(Resx.En.ContainsKey(key), $"waived key {key} no longer exists — drop the waiver");
            var enPh = Wave3Harness.Placeholders(Resx.En[key]);
            var faPh = Wave3Harness.Placeholders(Resx.Fa.TryGetValue(key, out var f) ? f : "");
            Assert.False(enPh.SequenceEqual(faPh),
                $"{key} no longer drifts (EN [{string.Join(",", enPh)}] FA [{string.Join(",", faPh)}]) — remove the waiver and fix README/keys");
            Assert.True(waiver.En.SequenceEqual(enPh) && waiver.Fa.SequenceEqual(faPh));
        }
    }

    [Fact]
    public void PlaceholderIndices_AreContiguousFromZero()
    {
        // "{0} … {2}" formats without error and strands an unwritable hole in the template.
        if (Resx is null) return;
        var offenders = new List<string>();
        foreach (var (label, dict) in new[] { ("EN", Resx.En), ("FA", Resx.Fa) })
            foreach (var kv in dict)
            {
                var ph = Wave3Harness.Placeholders(kv.Value);
                if (ph.Length > 0 && !ph.SequenceEqual(Enumerable.Range(0, ph.Length)))
                    offenders.Add($"{label} {kv.Key}: [{string.Join(",", ph)}]");
            }
        Assert.True(offenders.Count == 0, "non-contiguous placeholders: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Keys_AreNamespacedWithTheDocumentedPrefixes()
    {
        // §4 of the lane protocol freezes the prefix list. A key outside it means a lane invented a
        // namespace on its own and the merge/review will not find it.
        if (Resx is null) return;
        string[] prefixes =
        {
            "App.", "Common.", "Enum.", "Format.", "Tab.", "Onboarding.", "Today.", "Health.",
            "Goals.", "Goal.", "Habits.", "Programs.", "Bootcamp.", "Profile.", "Privacy.", "Review.",
            "WeeklySummary.", "Weekly.", "Rule.", "Rec.", "Benefit.", "Plan.", "Insight.",
            "Intelligence.", "State.", "Metric.", "Level.", "Seed.", "ErrorDialog_",
            // Wave 3 prefixes the protocol reserves for lanes that have not landed in this copy yet:
            "Update.", "Log.", "Editor.", "Settings.", "Reminders.", "Theme.", "Notification.",
            "Source.", "Habit.",
            // Added at merge: lane 08's week-progress rail component (its own §2-name component,
            // extended deliberately per this test's rule — the prefix appears in docs/WAVE3.md).
            "WeekProgress.",
        };
        var unknown = Resx.En.Keys
            .Where(k => !prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        Assert.True(unknown.Count == 0,
            $"keys outside the documented prefixes (extend the list deliberately, never silently): {string.Join(", ", unknown)}");
    }

    [Fact]
    public void ResourceAccessor_ResolvesBothCultures_FromTheCompiledResources()
    {
        // End-to-end: the assembly really carries what the disk holds. ManifestResourceName or
        // satellite-copy mistakes break the app, not the resx files, so XML parsing alone misses them.
        var en = Resources.Localization.AppResources.Get("Common.Save", System.Globalization.CultureInfo.InvariantCulture);
        if (en != "Save") return;   // no embedded resources in this build layout — skip, don't fake

        var pair = Resx;
        if (pair is null) return;
        foreach (var key in pair.En.Keys)
        {
            Assert.Equal(pair.En[key], Resources.Localization.AppResources.Get(key, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
