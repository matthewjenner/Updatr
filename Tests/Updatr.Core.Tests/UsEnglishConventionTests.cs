using System.Text.RegularExpressions;

namespace Updatr.Core.Tests;

/// <summary>
/// Everything written in this repository uses American spelling - code, comments, docs and scripts.
/// </summary>
/// <remarks>
/// <para>
/// <b>What breaks if this fails</b> is nothing at runtime, which is exactly why it needs a test. Mixed
/// spelling reads as carelessness, and it sits badly next to the BCL, which is American throughout
/// (<c>Color</c>, <c>OperationCanceledException</c>, <c>Initialize</c>). It drifts back in a word at a
/// time, and a word at a time is invisible in review.
/// </para>
/// <para>
/// <b>Ported from Klakr, which adapted it from the AtcSim repository.</b> The word list is shared
/// with both verbatim. <c>grey</c> is deliberately absent: popular packages use it as a token
/// (brush and color names), so flagging it would fight the libraries rather than the prose.
/// </para>
/// <para>
/// <b>The list is words, not a dictionary.</b> A general -ise/-our rule would flag legitimate identifiers
/// and quoted vendor text, so every word here is deliberate. Some have actually appeared and been corrected;
/// the rest are there to catch the next one before it lands.
/// </para>
/// <para>
/// <b>Append to this list; never replace it.</b> One rewrite meant to extend it dropped 63 of the 64
/// entries it was extending - including <c>centre</c>, the word that came back after this rule had been
/// written down for days and the reason the check exists at all. It still passed, because a check that has
/// stopped looking for something is indistinguishable from one that found nothing.
/// </para>
/// </remarks>
public sealed class UsEnglishConventionTests
{
    /// <summary>
    /// British spellings that have appeared here, and what each should be.
    /// </summary>
    /// <remarks>
    /// <c>cancelled</c>/<c>cancelling</c> are the double-L British forms; the American <c>canceled</c>
    /// and <c>canceling</c> pass, which is why the pattern cannot just look for the stem.
    /// </remarks>
    private static readonly (string Wrong, string Right)[] Words =
    [
        ("centre",              "center"),
        ("centred",             "centered"),
        ("centres",             "centers"),
        ("neighbour",           "neighbor"),
        ("neighbours",          "neighbors"),
        ("neighbouring",        "neighboring"),
        ("behaviour",           "behavior"),
        ("behaviours",          "behaviors"),
        ("colour",              "color"),
        ("colours",             "colors"),
        ("coloured",            "colored"),
        ("modelled",            "modeled"),
        ("modelling",           "modeling"),
        ("labelled",            "labeled"),
        ("labelling",           "labeling"),
        ("mislabelled",         "mislabeled"),
        ("cancelled",           "canceled"),
        ("cancelling",          "canceling"),
        ("travelled",           "traveled"),
        ("fuelled",             "fueled"),
        ("signalling",          "signaling"),
        ("analyse",             "analyze"),
        ("analysed",            "analyzed"),
        ("analysing",           "analyzing"),
        ("organise",            "organize"),
        ("organised",           "organized"),
        ("recognise",           "recognize"),
        ("recognised",          "recognized"),
        ("realise",             "realize"),
        ("realised",            "realized"),
        ("prioritise",          "prioritize"),
        ("summarise",           "summarize"),
        ("optimise",            "optimize"),
        ("initialise",          "initialize"),
        ("normalise",           "normalize"),
        ("serialise",           "serialize"),
        ("synchronise",         "synchronize"),
        ("apologise",           "apologize"),
        ("metre",               "meter"),
        ("metres",              "meters"),
        ("aeroplane",           "airplane"),
        ("aeroplanes",          "airplanes"),
        ("judgement",           "judgment"),
        ("acknowledgement",     "acknowledgment"),
        ("towards",             "toward"),
        ("whilst",              "while"),
        ("amongst",             "among"),
        ("maths",               "math"),
        ("programme",           "program"),
        ("licence",             "license"),
        ("defence",             "defense"),
        ("favour",              "favor"),
        ("favours",             "favors"),
        ("favoured",            "favored"),
        ("honour",              "honor"),
        ("flavour",             "flavor"),
        ("rumour",              "rumor"),
        ("endeavour",           "endeavor"),
        ("labour",              "labor"),
        ("humour",              "humor"),
        ("vapour",              "vapor"),
        ("armour",              "armor"),
        ("practise",            "practice"),
        ("serialisation",     "serialization"),
        ("serialisable",      "serializable"),
        ("serialiser",        "serializer"),
        ("deserialise",       "deserialize"),
        ("deserialised",      "deserialized"),
        ("deserialises",      "deserializes"),
        ("deserialisation",   "deserialization"),
        ("initialisation",    "initialization"),
        ("initialiser",       "initializer"),
        ("initialised",       "initialized"),
        ("normalisation",     "normalization"),
        ("normalised",        "normalized"),
        ("synchronisation",   "synchronization"),
        ("synchronised",      "synchronized"),
        ("organisation",      "organization"),
        ("organisational",    "organizational"),
        ("organiser",         "organizer"),
        ("optimisation",      "optimization"),
        ("optimised",         "optimized"),
        ("optimiser",         "optimizer"),
        ("prioritisation",    "prioritization"),
        ("prioritised",       "prioritized"),
        ("summarised",        "summarized"),
        ("realisation",       "realization"),
        ("recognisable",      "recognizable"),
        ("recognising",       "recognizing"),
        ("analyser",          "analyzer"),
        ("authorise",         "authorize"),
        ("authorised",        "authorized"),
        ("authorisation",     "authorization"),
        ("customise",         "customize"),
        ("customised",        "customized"),
        ("customisation",     "customization"),
        ("standardise",       "standardize"),
        ("standardised",      "standardized"),
        ("minimise",          "minimize"),
        ("minimised",         "minimized"),
        ("maximise",          "maximize"),
        ("maximised",         "maximized"),
        ("utilise",           "utilize"),
        ("utilised",          "utilized"),
        ("visualise",         "visualize"),
        ("visualised",        "visualized"),
        ("visualisation",     "visualization"),
        ("emphasise",         "emphasize"),
        ("emphasised",        "emphasized"),
        ("categorise",        "categorize"),
        ("categorised",       "categorized"),
        ("characterise",      "characterize"),
        ("characterised",     "characterized"),
        ("generalise",        "generalize"),
        ("generalised",       "generalized"),
        ("specialise",        "specialize"),
        ("specialised",       "specialized"),
        ("stabilise",         "stabilize"),
        ("stabilised",        "stabilized"),
        ("randomise",         "randomize"),
        ("randomised",        "randomized"),
        ("sanitise",          "sanitize"),
        ("sanitised",         "sanitized"),
        ("finalise",          "finalize"),
        ("finalised",         "finalized"),
        ("localise",          "localize"),
        ("localised",         "localized"),
        ("centralise",        "centralize"),
        ("centralised",       "centralized"),
        ("decentralised",     "decentralized"),
        ("itemise",           "itemize"),
        ("penalise",          "penalize"),
        ("penalised",         "penalized"),
        ("equalise",          "equalize"),
        ("parallelise",       "parallelize"),
        ("familiarise",       "familiarize"),
        ("memorise",          "memorize"),
        ("neutralise",        "neutralize"),
        ("paralyse",          "paralyze"),
        ("paralysed",         "paralyzed"),
        ("catalyse",          "catalyze"),
        ("levelling",         "leveling"),
        ("levelled",          "leveled"),
        ("marshalling",       "marshaling"),
        ("marshalled",        "marshaled"),
        ("refuelling",        "refueling"),
        ("refuelled",         "refueled"),
        ("fuelling",          "fueling"),
        ("signalled",         "signaled"),
        ("channelling",       "channeling"),
        ("channelled",        "channeled"),
        ("dialling",          "dialing"),
        ("dialled",           "dialed"),
        ("spiralling",        "spiraling"),
        ("spiralled",         "spiraled"),
        ("totalling",         "totaling"),
        ("totalled",          "totaled"),
        ("equalled",          "equaled"),
        ("funnelled",         "funneled"),
        ("tunnelled",         "tunneled"),
        ("unravelled",        "unraveled"),
        ("counsellor",        "counselor"),
        ("marvellous",        "marvelous"),
        ("jewellery",         "jewelry"),
        ("remodelled",        "remodeled"),
        ("relabelled",        "relabeled"),
        ("travelling",        "traveling"),
        ("enrolment",         "enrollment"),
        ("fulfil",            "fulfill"),
        ("fulfilment",        "fulfillment"),
        ("skilful",           "skillful"),
        ("wilful",            "willful"),
        ("appal",             "appall"),
        ("instalment",        "installment"),
        ("favourite",         "favorite"),
        ("favourable",        "favorable"),
        ("unfavourable",      "unfavorable"),
        ("behavioural",       "behavioral"),
        ("neighbourhood",     "neighborhood"),
        ("colourful",         "colorful"),
        ("colouring",         "coloring"),
        ("discolour",         "discolor"),
        ("harbour",           "harbor"),
        ("rigour",            "rigor"),
        ("vigour",            "vigor"),
        ("odour",             "odor"),
        ("clamour",           "clamor"),
        ("parlour",           "parlor"),
        ("splendour",         "splendor"),
        ("candour",           "candor"),
        ("saviour",           "savior"),
        ("labourer",          "laborer"),
        ("honourable",        "honorable"),
        ("tumour",            "tumor"),
        ("kilometre",         "kilometer"),
        ("kilometres",        "kilometers"),
        ("millimetre",        "millimeter"),
        ("millimetres",       "millimeters"),
        ("centimetre",        "centimeter"),
        ("centimetres",       "centimeters"),
        ("fibre",             "fiber"),
        ("litre",             "liter"),
        ("litres",            "liters"),
        ("theatre",           "theater"),
        ("offence",           "offense"),
        ("pretence",          "pretense"),
        ("licences",          "licenses"),
        ("licenced",          "licensed"),
        ("practising",        "practicing"),
        ("practised",         "practiced"),
        ("dependant",         "dependent"),
        ("dependants",        "dependents"),
        ("catalogue",         "catalog"),
        ("catalogues",        "catalogs"),
        ("catalogued",        "cataloged"),
        ("artefact",          "artifact"),
        ("artefacts",         "artifacts"),
        ("aluminium",         "aluminum"),
        ("tyre",              "tire"),
        ("tyres",             "tires"),
        ("storey",            "story"),
        ("storeys",           "stories"),
        ("draught",           "draft"),
        ("sceptical",         "skeptical"),
        ("scepticism",        "skepticism"),
        ("sulphur",           "sulfur"),
        ("enquire",           "inquire"),
        ("enquiry",           "inquiry"),
        ("speciality",        "specialty"),
        ("specialities",      "specialties"),
        ("cheque",            "check"),
        ("mould",             "mold"),
        ("moulded",           "molded"),
        ("smoulder",          "smolder"),
        ("plough",            "plow"),
        ("manoeuvrable",      "maneuverable"),
        ("manoeuvrability",   "maneuverability"),
        ("acknowledgements",  "acknowledgments"),
        ("judgements",        "judgments"),
        ("manoeuvre", "maneuver"),
        ("manoeuvres", "maneuvers"),
        ("manoeuvring", "maneuvering"),

        // Inflections of words already listed above. Matching is whole-word, so
        // "recognise" does not catch "recognises" and "honour" does not catch
        // "honouring" - both of which reached the codebase past this test.
        ("organises", "organizes"),
        ("organising", "organizing"),
        ("initialises", "initializes"),
        ("initialising", "initializing"),
        ("normalises", "normalizes"),
        ("normalising", "normalizing"),
        ("serialises", "serializes"),
        ("serialising", "serializing"),
        ("synchronises", "synchronizes"),
        ("synchronising", "synchronizing"),
        ("optimises", "optimizes"),
        ("optimising", "optimizing"),
        ("prioritises", "prioritizes"),
        ("prioritising", "prioritizing"),
        ("summarises", "summarizes"),
        ("summarising", "summarizing"),
        ("customises", "customizes"),
        ("customising", "customizing"),
        ("authorises", "authorizes"),
        ("authorising", "authorizing"),
        ("standardises", "standardizes"),
        ("standardising", "standardizing"),
        ("minimises", "minimizes"),
        ("minimising", "minimizing"),
        ("maximises", "maximizes"),
        ("maximising", "maximizing"),
        ("utilises", "utilizes"),
        ("utilising", "utilizing"),
        ("visualises", "visualizes"),
        ("visualising", "visualizing"),
        ("emphasises", "emphasizes"),
        ("emphasising", "emphasizing"),
        ("categorises", "categorizes"),
        ("categorising", "categorizing"),
        ("characterises", "characterizes"),
        ("characterising", "characterizing"),
        ("generalises", "generalizes"),
        ("generalising", "generalizing"),
        ("specialises", "specializes"),
        ("specialising", "specializing"),
        ("stabilises", "stabilizes"),
        ("stabilising", "stabilizing"),
        ("randomises", "randomizes"),
        ("randomising", "randomizing"),
        ("sanitises", "sanitizes"),
        ("sanitising", "sanitizing"),
        ("finalises", "finalizes"),
        ("finalising", "finalizing"),
        ("localises", "localizes"),
        ("localising", "localizing"),
        ("centralises", "centralizes"),
        ("centralising", "centralizing"),
        ("itemises", "itemizes"),
        ("itemising", "itemizing"),
        ("penalises", "penalizes"),
        ("penalising", "penalizing"),
        ("apologises", "apologizes"),
        ("apologising", "apologizing"),
        ("realises", "realizes"),
        ("realising", "realizing"),
        ("recognises", "recognizes"),
        ("honouring", "honoring"),
        ("honoured", "honored"),
        ("favouring", "favoring"),
        ("flavouring", "flavoring"),
        ("flavours", "flavors"),
        ("flavoured", "flavored"),
        ("labouring", "laboring"),
        ("labours", "labors"),
        ("laboured", "labored"),
        ("rumouring", "rumoring"),
        ("rumours", "rumors"),
        ("rumoured", "rumored"),
        ("endeavouring", "endeavoring"),
        ("endeavours", "endeavors"),
        ("endeavoured", "endeavored"),
        ("vapouring", "vaporing"),
        ("vapours", "vapors"),
        ("vapoured", "vapored"),
        ("armouring", "armoring"),
        ("armours", "armors"),
        ("armoured", "armored"),
        ("harbouring", "harboring"),
        ("harbours", "harbors"),
        ("harboured", "harbored"),
        ("odouring", "odoring"),
        ("odours", "odors"),
        ("odoured", "odored"),
        ("clamouring", "clamoring"),
        ("clamours", "clamors"),
        ("clamoured", "clamored"),
        ("honours", "honors"),
        ("practises", "practices"),
    ];

    /// <summary>
    /// Files that legitimately carry one of these words.
    /// </summary>
    /// <remarks>
    /// Only this file, which has to spell out every word it is looking for. Nothing else belongs
    /// here - an exemption is how the rule quietly stops applying.
    /// </remarks>
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        "UsEnglishConventionTests.cs",
    };

    [Fact]
    public void NothingInTheRepositoryUsesBritishSpelling()
    {
        Regex pattern = new Regex(
            @"\b(" + string.Join("|", Array.ConvertAll(Words, word => word.Wrong)) + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        Dictionary<string, string> corrections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string wrong, string right) in Words)
        {
            corrections[wrong] = right;
        }

        string repository = RepositoryPaths.Resolve(".");
        List<string> offenders = [];

        foreach (string file in RepositoryFiles.Authored())
        {
            if (Exempt.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in pattern.Matches(lines[i]))
                {
                    offenders.Add(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "{0}:{1}  {2} -> {3}",
                        Path.GetRelativePath(repository, file),
                        i + 1,
                        match.Value,
                        corrections[match.Value]));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} British spelling(s). This repository is US English throughout - code, "
                + "comments, docs and commit messages:\n  "
                + string.Join("\n  ", offenders));
    }
}
