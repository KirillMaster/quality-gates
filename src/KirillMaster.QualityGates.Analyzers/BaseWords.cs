using System.Collections.Generic;

namespace KirillMaster.QualityGates.Analyzers;

internal static class BaseWords
{
    private const string Words =
        "a about above accept access account action active actual add address after again against age agent all allow alone already also always amount an and any api app apply approve area argument array as ask assert assign at attempt attribute authorize auto available average back backup bad balance bank base basic batch be because before begin behavior below best between bill binary bit block body bool boolean both bound box branch break buffer build builder bundle business but button by cache calculate call callback can cancel candidate capacity card carry cart case cast catalog catch category cell chain change channel char character charge check child choice chunk class clean clear client clock clone close code collect collection color column combine command comment commit common company compare complete component compute concat condition config configuration connect connection consume consumer contain content context continue control convert copy core correct cost count counter country cover create created credit criteria culture currency current custom customer cycle data database date day deadline debug decimal decision declare decode default define delay delete deliver delivery dependency deploy depth describe description design destination detail detect device dictionary diff digit direct directory disable discount dispatch display distinct divide document domain done double down download draft driver drop dry duplicate duration dynamic each early edge edit effect element else email empty enable encode end endpoint engine enough ensure enter entity entry enum environment equal error escape estimate evaluate event every exact example except exception exchange execute exist expect expire explicit export expression extension external extra extract factory fail failure fake false fast feature feed fetch field file fill filter final find finish first fix flag flat float flow flush folder follow for force form format forward found fragment frame free from full function future gate general generate generic get give global go good grant graph greater group guard guid handle handler hash have head header health height help helper hidden hide high history hit hold home host hour http id identity idle if ignore image immutable implement import in include income increase index info information init initial inner input insert inside install instance integer integration interface internal interval invalid invoice invoke is issue item iterate job join json key kind know label language large last late layer lazy lead left legacy length less level library life like limit line link list listen literal live load local lock log logic long look loop low main make manage manager map mark mask match max maximum measure memory merge message meta method metric middle migrate min minimum minute miss missing mock mode model modify module money monitor month more move multi multiple must name namespace native need negative nested network new next no node none normal not note notice notify now null number object observe offset old on once only open operation option or order origin other out output over override owner pack package pad page pair parallel param parameter parent parse part partial partner pass password past patch path pattern pause pay payload payment pending percent period permission persist phase phone pick pipe pipeline place plan platform plugin point policy pool port position positive post power prefix prepare present press previous price primary print priority private process produce product profile program progress project promise property protocol provider proxy public publish pull purchase push put query queue quick quota raise random range rate raw reach read ready real reason receive recent record recover reduce reference refresh region register reject related release reload remain remote remove render repeat replace reply report repository represent request require reset resolve resource response rest restore result resume retry return reverse review revoke right role rollback root round route row rule run runtime safe sample save scale scan schedule schema scope score script search second secret section secure seed select send sender sequence serialize server service session set setting setup shape share shift ship short should show shutdown side sign signal simple single site size skip sleep slice slot small snapshot sort source space span spec special specific split stack stage standard start state static status step stop storage store stream string strategy strict structure style sub subject submit subscribe success sum summary supplier support suppress swap switch symbol sync system table tag take target task template temp term test text then there thread threshold throw tick time timeout timer title to token tool top total trace track transaction transfer transform translate tree trigger trim true trust try tuple type unique unit unknown unlock unregister until up update upload url use user utc valid validate validation value variable vendor verify version view virtual visible visit void wait walk warn warning watch web week weight when where while whole width window with within without work worker wrap write wrong xml year yes zero zip zone";

    public static readonly ImmutableHashSet<string> Set = Words.Split(' ').ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool Knows(string word, ImmutableHashSet<string> extra) =>
        Candidates(word.ToLowerInvariant()).Any(c => Set.Contains(c) || extra.Contains(c));

    private static IEnumerable<string> Candidates(string word)
    {
        yield return word;
        foreach (var suffix in new[] { "s", "es", "ed", "d", "ing", "er", "ers", "or", "ors", "ly", "ion", "ions", "ment", "ments", "able", "ness", "al", "ity" })
        {
            if (word.Length > suffix.Length + 2 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                var stem = word.Substring(0, word.Length - suffix.Length);
                yield return stem;
                yield return stem + "e";
                if (stem.Length > 2 && stem[stem.Length - 1] == stem[stem.Length - 2])
                {
                    yield return stem.Substring(0, stem.Length - 1);
                }

                if (stem.EndsWith("i", StringComparison.Ordinal))
                {
                    yield return stem.Substring(0, stem.Length - 1) + "y";
                }
            }
        }
    }
}
