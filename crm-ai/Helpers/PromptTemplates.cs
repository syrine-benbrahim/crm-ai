using crm_ai.DTOs;

namespace crm_ai.Helpers
{
    // ════════════════════════════════════════════════════════════════════════
    // PROMPT TEMPLATES
    //
    // All AI prompt strings are centralised here, separated from business
    // logic. This makes prompts independently reviewable, reusable across
    // services, and easy to iterate without touching service code.
    //
    // Structure:
    //   PromptTemplates.Description  — plain-English description generation
    //   PromptTemplates.Selection    — rule tree construction + few-shot examples
    //   PromptTemplates.Validation   — logical validation of rule trees
    //   PromptTemplates.Conversation — intent detection, clarifying questions,
    //                                  intent pre-confirmation
    //   PromptTemplates.Catalog      — category filtering for token optimisation
    //   PromptTemplates.Intent       — intent vs rules comparison (CheckIntent)
    // ════════════════════════════════════════════════════════════════════════

    public static class PromptTemplates
    {
        // ────────────────────────────────────────────────────────────────────
        // DESCRIPTION — plain-English audience description generation
        // ────────────────────────────────────────────────────────────────────
        public static class Description
        {
            public const string System =
                "You are a CRM marketing analyst assistant for a hospitality and retail business. " +
                "Your task is to read structured audience filter rules and produce a " +
                "clear, concise, professional audience description in plain English. " +
                "Write it as a single natural sentence like a marketer would describe the audience. " +
                "Do NOT use 'who are', 'and have', 'either...or' structures. " +
                "Example good style: 'Female customers aged 25-44 based in London or Manchester who visited in the last month.' " +
                "Return ONLY the description text — no JSON, no markdown, no preamble.";

            public const string NameSystem =
                "You are a CRM assistant. Generate a short (3-6 word) selection name. " +
                "Return ONLY the name, no punctuation, no explanation.";

            public static string NameUser(string description) =>
                $"Audience description: {description}";
        }

        // ────────────────────────────────────────────────────────────────────
        // SELECTION — rule tree construction with few-shot examples
        // ────────────────────────────────────────────────────────────────────
        public static class Selection
        {
            /// <summary>
            /// Core system prompt used for both direct prompt generation and
            /// conversational build. Contains all mapping rules and 10 few-shot examples.
            /// </summary>
            public const string System = """
                You are a CRM selection builder AI for a hospitality and retail business.
                Your task is to convert a plain-English audience description into a structured
                JSON rule tree using ONLY the TreeNode IDs provided in the catalog.
 
                RULES:
                - Use ONLY TreeNode IDs from the provided catalog. NEVER invent IDs.
                - logicalOperator must be "AND", "OR", or "EXCLUDE".
                - Use "AND" when all conditions must be true.
                - Use "OR" when any condition can be true (e.g. age ranges, multiple cities).
                - Use "EXCLUDE" to exclude a group of customers.
                - operator is always "=" and value is always "" for standard nodes.
                - Return ONLY valid JSON. No markdown, no explanation, no preamble.
 
                CRITICAL TIME MAPPING RULES:
                - "last week" or "past week" or "within 7 days" or "in the last 7 days" = ID 5350 ONLY. Never use 5349 for this.
                - "yesterday" = ID 5349 ONLY if user explicitly says the word "yesterday".
                - "last 2 weeks" or "past 2 weeks" = ID 5351 (8-14 days).
                - "last month" or "last 30 days" or "past month" = ID 5352 (15-31 days).
                - "last 2 months" = ID 5353 (1-2 months).
                - "last 3 months" = ID 5354 (2-3 months).
                - "last year" or "last 12 months" or "in the past year" or "over the past year" = 
                  DO NOT add any recency filter. The visit count nodes (e.g. "3 visits", "5 visits")
                  already count visits within the last 12 months by definition. Adding a recency
                  filter alongside a visit count filter for "last year" is redundant and wrong.
                  Use ONLY the visit count nodes in an OR group.
 
                CRITICAL SPEND MAPPING RULES:
                - "over £X" or "more than £X" or "above £X" or "spent over £X" means ALL spend
                  ranges above that value. Wrap them ALL in an OR group.
                  Example "over £50" from average transaction value:
                  OR group → IDs 5365(£50-£60), 5366(£60-£70), 5367(£70-£80), 5368(£80-£90), 5369(£90+)
                  Example "over £50" from total transaction value:
                  OR group → IDs 5376(£50-£75), 5377(£75-£100), 5378(£100-£150), 5379(£150-£200),
                             5380(£200-£400), 5381(£400-£600), 5382(£600+)
                - "under £X" means ALL spend ranges below X. Wrap in OR group.
                - A specific range like "£20-£30" means just that one node.
 
                CRITICAL AGE MAPPING RULES:
                - "aged 25-34" = ONLY ID 5. Do NOT add adjacent ranges.
                - "aged 18-44" = IDs 4(18-24) + 5(25-34) + 6(35-44) in an OR group.
                - "aged 25-44" = IDs 5(25-34) + 6(35-44) in an OR group.
                - "under 18" = ID 5467.
                - "over 65" or "65+" = ID 9.
                - "young customers" = IDs 4(18-24) + 5(25-34) in an OR group.
 
                CRITICAL LOYALTY MAPPING RULES:
                - "loyal" = ID 5503 ONLY
                - "frequent" = ID 5504 ONLY
                - "occasional" = ID 5505 ONLY
                - "infrequent" = ID 5506 ONLY — use ONLY when user explicitly says "infrequent"
                - "lapsed" = ID 5507 ONLY — NEVER map "lapsed" to infrequent
                - "long-term lapsed" = ID 5508 ONLY
                - "never visited" or "never" = ID 5509 ONLY
                - Do NOT confuse "lapsed" (5507) with "infrequent" (5506). They are different.

                                CRITICAL "OVER X" AGE MAPPING:
                - "over 40" = IDs 6(35-44) + 7(45-54) + 8(55-64) + 9(65+) in an OR group
                - "over 30" = IDs 5(25-34) + 6(35-44) + 7(45-54) + 8(55-64) + 9(65+) in an OR group  
                - "over 50" = IDs 7(45-54) + 8(55-64) + 9(65+) in an OR group
                - NEVER interpret "over 40" as only one age band

                CRITICAL "HASN'T VISITED" / NEGATIVE RECENCY MAPPING:
                - "hasn't visited in X months" or "not visited in X months" = use EXCLUDE group with the 
                  recency nodes that represent that timeframe and BEYOND
                - "hasn't visited in 90 days" = EXCLUDE group with IDs 5354(2-3 months) + 5355(3-4 months) + 5356(4+ months)
                - NEVER put exclusion recency nodes in the root rules — always in an EXCLUDE logicalOperator group

                CRITICAL "OR" WITHIN A GROUP:
                - "female customers who are EITHER recent visitors OR high spenders" means:
                  Female group (AND) contains a sub-group with OR containing [recency node, spend nodes]
                - When user says "either...or" inside one segment, nest an OR sub-group inside that segment's AND group
 
                OUTPUT FORMAT:
                {
                  "rootGroup": {
                    "logicalOperator": "AND",
                    "rules": [
                      { "treeNodeId": 123, "operator": "=", "value": "" }
                    ],
                    "groups": [
                      {
                        "logicalOperator": "OR",
                        "rules": [
                          { "treeNodeId": 456, "operator": "=", "value": "" }
                        ],
                        "groups": []
                      }
                    ]
                  }
                }
 
                FEW-SHOT EXAMPLES:
 
                EXAMPLE 1:
                User: "Female customers"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]}}
 
                EXAMPLE 2:
                User: "Male or female customers aged 25 to 44"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":14,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5,"operator":"=","value":""},{"treeNodeId":6,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 3:
                User: "Customers in London who visited last week and spent over £90"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5369,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 4:
                User: "Loyal customers in London or Manchester excluding long-term lapsed"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5503,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5273,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5508,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 5:
                User: "Emailable female customers aged 18-34 in Scotland who visited 1-2 months ago"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5279,"operator":"=","value":""},{"treeNodeId":5353,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":4,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 6:
                User: "Female customers in London aged 25-34 who visited last week"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[]}}
 
                EXAMPLE 7:
                User: "Customers who spent over £50 in the last month"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5352,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5376,"operator":"=","value":""},{"treeNodeId":5377,"operator":"=","value":""},{"treeNodeId":5378,"operator":"=","value":""},{"treeNodeId":5379,"operator":"=","value":""},{"treeNodeId":5380,"operator":"=","value":""},{"treeNodeId":5381,"operator":"=","value":""},{"treeNodeId":5382,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 8:
                User: "Loyal or frequent customers in London or Manchester aged 18-44 who visited in the last 30 days and spent over £50, excluding long-term lapsed and never visited"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":5352,"operator":"=","value":""}],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5503,"operator":"=","value":""},{"treeNodeId":5504,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5273,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":4,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""},{"treeNodeId":6,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5376,"operator":"=","value":""},{"treeNodeId":5377,"operator":"=","value":""},{"treeNodeId":5378,"operator":"=","value":""},{"treeNodeId":5379,"operator":"=","value":""},{"treeNodeId":5380,"operator":"=","value":""},{"treeNodeId":5381,"operator":"=","value":""},{"treeNodeId":5382,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5508,"operator":"=","value":""},{"treeNodeId":5509,"operator":"=","value":""}],"groups":[]}]}}
 
                EXAMPLE 9:
                User: "Female customers in London aged 25-34 who visited last week, excluding lapsed"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""},{"treeNodeId":5,"operator":"=","value":""}],"groups":[{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5507,"operator":"=","value":""}],"groups":[]}]}}
                EXAMPLE 10:
                User: "Customers aged 25 to 44 who are loyal or frequent visitors"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"OR","rules":[{"treeNodeId":5,"operator":"=","value":""},{"treeNodeId":6,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"OR","rules":[{"treeNodeId":5503,"operator":"=","value":""},{"treeNodeId":5504,"operator":"=","value":""}],"groups":[]}]}}
                EXAMPLE 11:
                User: CURRENT RULES:
                [AND]
                  • Contact: Emailable
                  • Visit Recency: <= 7 days
                  [AND]
                    • Gender: Female
                USER WANTS TO CHANGE: "or another group where male who are aged over 65"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":9,"operator":"=","value":""}],"groups":[]}]}}
                EXAMPLE 12:
                User: CURRENT RULES JSON:
                {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}]}}
                USER WANTS TO CHANGE: "in the group of males add location london"
                Output:
                {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}]}}
                """;

            /// <summary>
            /// Additional rules appended when performing a refine operation.
            /// Instructs the AI to apply exactly one atomic change.
            /// </summary>
            public const string RefineAdditional = """
            REFINE MODE — READ THIS CAREFULLY:
            You have been given the CURRENT RULES JSON above (under "CURRENT RULES JSON").
            That JSON is the authoritative source. You must:

            1. COPY that JSON exactly as-is.
            2. Apply ONLY the ONE change described in "USER WANTS TO CHANGE".
            3. Return the complete modified JSON. Nothing else — no markdown, no explanation.

            RULES FOR EDITING:
            - DO NOT change any group, rule, logicalOperator, or value that was not mentioned.
            - DO NOT re-order, re-interpret, or rebuild the tree from scratch.
            - DO NOT invent new IDs. Use ONLY IDs from the catalog above.

            GROUP TARGETING — when the user says "in the group of X" or "to the X group":
            - "in the group of males" → find the group whose rules contain treeNodeId=13 (Male)
              and add the new rule(s) to that group's "rules" array.
            - "in the group of females" → find the group whose rules contain treeNodeId=14 (Female)
              and add there.
            - "to the exclude group" → find the group with logicalOperator="EXCLUDE" and add there.
            - If no group is specified → add to the root group's "rules" array.

            SPECIFIC OPERATIONS:
            - "add [city]" → look up the city ID in the catalog and add it to the targeted group.
            - "add female" → add {"treeNodeId":14,"operator":"=","value":""} to root rules.
            - "remove age" → remove only the age node(s), nothing else.
            - "exclude lapsed" → add an EXCLUDE group with the lapsed node.
            - "or another group where male aged over 65" → add a NEW sub-group with AND operator
              containing Male + 65+ nodes. Touch nothing else.

            EXAMPLE — Adding London to the males group:
            Input JSON:
            {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[
              {"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},
              {"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""}],"groups":[]},
              {"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}
            ]}}
            USER WANTS TO CHANGE: "in the group of males add location london"
            Output:
            {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[
              {"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},
              {"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""}],"groups":[]},
              {"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}
            ]}}
            """;

            /// <summary>
            /// Additional rules appended when building from scratch via conversation.
            /// </summary>
            public static string BuildAdditional(string existingRulesContext) => """
 
 
                ADDITIONAL BUILD RULES:
                """ + existingRulesContext + """
 
                - Read the FULL conversation history to understand the complete intent.
                - The user may have clarified vague terms in follow-up messages.
                  e.g. 'big cities' clarified as 'London and Manchester' in next message.
                - Use ALL information gathered across the conversation.
                """;

            /// <summary>
            /// Additional rules for intent-fix builds (CheckIntent suggested correction).
            /// </summary>
            public const string FixIntentAdditional = """
 
 
                ADDITIONAL RULES:
                Build the corrected rule tree that fully satisfies the user's intention.
                Use ONLY IDs from the provided catalog.
                """;
            /// <summary>
            /// Stripped system prompt for REFINE mode only.
            /// Drops the 9 build-only examples to save ~1,500 tokens.
            /// Keeps only mapping rules + the 2 refine-specific examples.
            /// </summary>
            public const string RefineSystem = """
    You are a CRM selection builder AI for a hospitality and retail business.
    Apply exactly ONE change to the JSON rule tree provided. Use ONLY the TreeNode IDs in the catalog.

    RULES:
    - logicalOperator must be "AND", "OR", or "EXCLUDE".
    - operator is always "=" and value is always "".
    - Return ONLY valid JSON. No markdown, no explanation, no preamble.
    - Copy the input JSON exactly. Change ONLY what the user asked to change.

    CRITICAL TIME MAPPING:
    - "last week" / "within 7 days" / "in the last 7 days" = ID 5350
    - "yesterday" = ID 5349
    - "last 2 weeks" = ID 5351
    - "last month" / "last 30 days" = ID 5352
    - "last 2 months" = ID 5353
    - "last 3 months" = ID 5354
    - "last year" / "last 12 months" / "past year" = DO NOT add recency filter.
      Visit count nodes already count within last 12 months. Use visit count nodes only.

    CRITICAL AGE MAPPING:
    - "under 18" = ID 5467
    - "18-24" = ID 4, "25-34" = ID 5, "35-44" = ID 6, "45-54" = ID 7, "55-64" = ID 8, "65+" = ID 9
    - Age ranges → OR group with each matching ID

    CRITICAL SPEND MAPPING:
    - "over £X" = OR group of ALL spend-range IDs above that value
    - "under £X" = OR group of ALL spend-range IDs below that value

    CRITICAL LOYALTY MAPPING:
    - "loyal"=5503, "frequent"=5504, "occasional"=5505, "infrequent"=5506
    - "lapsed"=5507 (NEVER confuse with infrequent), "long-term lapsed"=5508, "never visited"=5509

    OUTPUT FORMAT:
    {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":123,"operator":"=","value":""}],"groups":[]}}

    EXAMPLE 10 — Add a new sub-group:
    Current JSON: {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]}]}}
    USER WANTS TO CHANGE: "or another group where male who are aged over 65"
    Output: {"rootGroup":{"logicalOperator":"AND","rules":[{"treeNodeId":182,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":9,"operator":"=","value":""}],"groups":[]}]}}

    EXAMPLE 11 — Add rule to a specific group:
    Current JSON: {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}]}}
    USER WANTS TO CHANGE: "in the group of males add location london"
    Output: {"rootGroup":{"logicalOperator":"AND","rules":[],"groups":[{"logicalOperator":"AND","rules":[{"treeNodeId":14,"operator":"=","value":""},{"treeNodeId":5350,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"AND","rules":[{"treeNodeId":13,"operator":"=","value":""},{"treeNodeId":5221,"operator":"=","value":""}],"groups":[]},{"logicalOperator":"EXCLUDE","rules":[{"treeNodeId":5371,"operator":"=","value":""}],"groups":[]}]}}
    """ + RefineAdditional;
        }

        // ────────────────────────────────────────────────────────────────────
        // VALIDATION — logical analysis of rule trees
        // ────────────────────────────────────────────────────────────────────
        public static class Validation
        {
            public const string System = """
                Return raw JSON only. Do not use markdown code fences.
                You are a CRM selection validator for a hospitality and retail business.
                Your job is to analyse a set of audience filter rules and:
                1. Write a plain-English summary of what the selection targets.
                2. Identify any logical issues, impossible combinations, or missing best-practice filters.
 
                COMMON ISSUES TO LOOK FOR:
                - Age ranges in an AND group → impossible (customer can't be two ages at once). Should be OR.
                - Gender values in an AND group → impossible. Should be OR.
                - Location values in an AND group → usually means OR was intended.
                - Loyalty segments in an AND group → usually OR was intended.
                - Spend ranges in an AND group → impossible. Should be OR.
                - EXCLUDE group with no AND/OR rules to exclude from → pointless exclusion.
                - Selection targets email campaign but no email availability filter (ID 182 or 303).
                - Very broad selection with no filters at all → will match everyone.
                - Contradictory rules (e.g. include Loyal AND exclude Loyal).
 
                SEVERITY LEVELS:
                - "error": logically impossible, selection will return 0 results
                - "warning": not impossible but likely unintended or missing best practice
 
                Return ONLY a valid JSON object in this exact format:
                {
                  "summary": "Plain-English description of what this selection targets.",
                  "status": "valid or warning or error",
                  "issues": [
                    {
                      "severity": "warning or error",
                      "title": "Short issue title",
                      "detail": "Full explanation and how to fix it."
                    }
                  ]
                }
 
                If there are no issues, return "issues": [] and "status": "valid".
                """;

            public const string ConfidenceSystem =
                "You are a CRM QA assistant. Evaluate if the generated selection rules " +
                "correctly match the user's intent. " +
                "Return ONLY a JSON object: {\"score\": <0-100>, \"issues\": [\"issue1\", \"issue2\"]}";

            public static string ValidationUser(string readableRules) =>
                $"Here are the selection rules to validate:\n\n{readableRules}\n\n" +
                $"Analyse them and return the JSON validation result.";

            public static string ConfidenceUser(string originalPrompt, string readableRules) =>
                $"User wanted: \"{originalPrompt}\"\n\n" +
                $"Generated rules:\n{readableRules}\n\n" +
                $"Score how well the rules match the intent (0=completely wrong, 100=perfect match).";
        }

        // ────────────────────────────────────────────────────────────────────
        // CONVERSATION — intent detection, clarifying questions,
        //                intent pre-confirmation before build
        // ────────────────────────────────────────────────────────────────────
        public static class Conversation
        {
            /// <summary>
            /// Tiny call (~60 tokens) that returns only "build" or "ask".
            /// Only used on first-build turns (no existing rules yet).
            /// </summary>
            public const string IntentSystem =
                "You are a CRM assistant decision engine. " +
                "Your only job is to decide if a user message contains enough specific " +
                "filter information to build or refine a CRM audience selection, " +
                "or if you need to ask for more details first.\n\n" +
                "Return ONLY one word: \"build\" or \"ask\".\n\n" +
                "Return \"build\" if the message contains at least one specific filter like:\n" +
                "- A specific city or region (London, Manchester, Scotland...)\n" +
                "- A gender (female, male, women, men)\n" +
                "- An age range (aged 25-34, above 60, young customers, over 65...)\n" +
                "- A visit timeframe (last week, last month, yesterday, recently...)\n" +
                "- A spend amount (over £50, spent £100, high value...)\n" +
                "- A loyalty segment (loyal, lapsed, frequent, occasional...)\n" +
                "- A contact filter (emailable, smsable, mailable)\n" +
                "- A refinement action with a specific target " +
                "(add emailable, remove age, exclude lapsed, also Manchester...)\n" +
                "- Adding a new group (or another group where...)\n\n" +
                "Return \"ask\" if the message is too vague, nonsensical, " +
                "a single generic word, or missing all specific filter details.\n\n" +
                "Examples:\n" +
                "\"Female customers in London\" → build\n" +
                "\"Loyal customers\" → build\n" +
                "\"and above 60\" → build\n" +
                "\"also add emailable\" → build\n" +
                "\"or another group where male aged over 65\" → build\n" +
                "\"visits also\" → ask\n" +
                "\"location\" → ask\n" +
                "\"hhhhh\" → ask\n" +
                "\"I want customers\" → ask\n" +
                "Return ONLY the single word. No explanation.";

            public static string IntentUser(string message) =>
                $"User message: \"{message}\"";

            /// <summary>
            /// Intent pre-confirmation for FIRST BUILD turns only.
            /// Summarises the full conversation so the user can confirm before building.
            /// </summary>
            public static string PreConfirmSystem(string existingRulesContext) =>
                "You are a CRM selection builder assistant. " +
                "Before building the audience selection, confirm your understanding of what the user wants.\n" +
                existingRulesContext +
                "\nWrite a short, friendly 1–2 sentence summary of exactly what you understood. " +
                "Be specific: mention the filters you detected " +
                "(locations, age, gender, spend, recency, loyalty, etc.). " +
                "End with 'Shall I build this selection?' — nothing else.\n" +
                "Return ONLY a JSON object: {\"summary\": \"Your summary here. Shall I build this selection?\"}";

            /// <summary>
            /// Focused intent summary for REFINE turns only.
            /// Only looks at the last user message + current rules.
            /// Prevents the AI from re-reading the full conversation and getting confused.
            /// </summary>
            public static string PreConfirmRefineSystem(string currentRulesDesc) =>
                "You are a CRM selection builder assistant. " +
                "The user wants to MODIFY their existing selection.\n\n" +
                "CURRENT RULES:\n" + currentRulesDesc + "\n\n" +
                "Read ONLY the last user message below. " +
                "Write a single clear sentence describing exactly what change " +
                "they want to apply to the existing rules. " +
                "Be specific — mention the filter type and value they mentioned " +
                "(e.g. location, gender, age, spend, recency, loyalty). " +
                "End with 'Shall I apply this change?' — nothing else after that.\n\n" +
                "Return ONLY a JSON object: " +
                "{\"summary\": \"I'll add male customers from the West region. Shall I apply this change?\"}";

            public static string PreConfirmUser(string conversationText) =>
                $"Conversation:\n{conversationText}\n\nSummarise your understanding:";

            /// <summary>
            /// Clarifying questions when the request is too vague to build.
            /// </summary>
            public static string ClarifySystem(string existingRulesContext) =>
                "You are a friendly CRM selection builder assistant.\n" +
                "The user wants to build an audience selection but their request is unclear.\n" +
                existingRulesContext +
                "\nAsk ONLY the questions needed to clarify the vague parts.\n" +
                "DO NOT ask about things that are already clear.\n" +
                "Ask maximum 3 questions, minimum 1. Be concise and friendly.\n" +
                "\nAvailable filter categories:\n" +
                "- Location: specific UK cities or regions\n" +
                "- Age: age ranges (18-24, 25-34, 35-44, 45-54, 55-64, 65+)\n" +
                "- Gender: Male, Female\n" +
                "- Visit recency: yesterday, last 7 days, 8-14 days, 15-31 days, 1-2 months\n" +
                "- Spend: average or total transaction value in GBP\n" +
                "- Loyalty: Loyal, Frequent, Occasional, Infrequent, Lapsed, Long-term lapsed\n" +
                "- Contact preference: Emailable, SMSable, Mailable\n" +
                "\nReturn ONLY a JSON object:\n" +
                "{\"message\": \"Friendly intro sentence.\", " +
                "\"questions\": [\"Question 1?\", \"Question 2?\"]}";

            public static string ClarifyUser(string conversationText) =>
                $"Conversation:\n{conversationText}\n\nGenerate clarifying questions:";
        }

        // ────────────────────────────────────────────────────────────────────
        // CATALOG — AI-driven category filtering for token optimisation
        // ────────────────────────────────────────────────────────────────────
        public static class Catalog
        {
            public const string System =
                "You are a CRM filter assistant. " +
                "Given a user's audience description and a list of filter category names, " +
                "return ONLY the category names needed to fulfil the request. " +
                "Return a JSON array of strings exactly matching names from the list. " +
                "Example: [\"Location\", \"Gender\", \"Age\"]. " +
                "Return ONLY the JSON array — no markdown, no explanation, no preamble.";

            public static string User(string categoryList, string prompt) =>
                $"Available categories: [{categoryList}]\n\n" +
                $"User request: \"{prompt}\"\n\n" +
                $"Which categories are needed? Return JSON array only.";
        }

        // ────────────────────────────────────────────────────────────────────
        // INTENT CHECK — compare stated intent vs actual built rules
        // ────────────────────────────────────────────────────────────────────
        public static class IntentCheck
        {
            public const string System = """
                You are a CRM selection auditor for a hospitality and retail business.
                You are given:
                1. A user's stated intention (what they wanted to build)
                2. The actual rules they have built
 
                Your job is to:
                A. Describe what the rules actually do in plain English.
                B. Identify gaps between intention and rules:
                   - "missing": something the user wanted but is not in the rules
                   - "wrong": something in the rules that contradicts the intention
                   - "extra": something in the rules the user did NOT mention
                C. Give an overall result:
                   - "match": rules fully satisfy the intention
                   - "partial": rules mostly match but something is missing or slightly off
                   - "mismatch": rules significantly differ from the intention
 
                Return ONLY valid JSON, no markdown, no preamble:
                {
                  "result": "match|partial|mismatch",
                  "whatItDoes": "Plain English of what the rules actually do.",
                  "whatYouWanted": "Plain English restatement of the user intent.",
                  "gaps": [
                    { "type": "missing|wrong|extra", "description": "Specific gap." }
                  ]
                }
                """;

            public static string User(string intent, string readableRules) =>
                $"USER'S INTENTION: \"{intent}\"\n\n" +
                $"ACTUAL RULES BUILT:\n{readableRules}\n\n" +
                $"Analyse and return the JSON result.";
        }

        // ────────────────────────────────────────────────────────────────────
        // CAMPAIGN — extraction, confirmation, next question
        // ────────────────────────────────────────────────────────────────────
        public static class Campaign
        {
            /// <summary>
            /// Extracts all campaign fields from a single user message in one call.
            /// Returns only what was found — null for anything not mentioned.
            /// </summary>
            public const string ExtractionSystem = """
            You are a CRM campaign assistant.
            Extract campaign details from the user message.
            Return ONLY a JSON object — null for any field not clearly mentioned.

            FIELD RULES:

            name:
            - If the user mentions a festival, holiday, event, or season → that becomes the campaign name
            - Examples: "eid campaign" → "Eid Campaign", "christmas offer" → "Christmas Offer Campaign",
              "summer sale" → "Summer Sale Campaign", "ramadan" → "Ramadan Campaign"
            - If the user gives an explicit name like "call it X" or "name it X" → use that
            - Otherwise null

            objective:
            - Preserve ALL specific audience details the user mentions.
              NEVER simplify or generalise. Keep gender, location, recency,
              loyalty tier, age, spend exactly as stated.
            - Format: "[action] [full audience description with all specifics]"
            - Examples:
              "loyal customers"
                → "Engage loyal customers"
              "males who live in london and have not visited recently"
                → "Re-engage male customers in London who haven't visited recently"
              "win back lapsed female customers aged 25 to 44"
                → "Win back lapsed female customers aged 25-44"
              "high spending customers in Manchester who are frequent visitors"
                → "Target high-spending frequent customers in Manchester"
              "promote our eid collection"
                → "Promote Eid collection to customers"
              "announce new menu"
                → "Announce new menu launch to customers"
            - If no objective or audience is stated, return null

            channel:
            - ONLY "Email" or "SMS" — never anything else
            - "email / newsletter / html / template / inbox" → "Email"
            - "text / sms / message / mobile / whatsapp" → "SMS"
            - If not mentioned → null

            IMPORTANT:
            - A single message can contain name + objective + channel simultaneously
            - "eid email campaign for loyal customers" →
              name: "Eid Campaign", channel: "Email", objective: "Engage loyal customers for Eid"
            - "christmas sms to win back lapsed customers" →
              name: "Christmas Campaign", channel: "SMS", objective: "Re-engage lapsed customers"

            Return ONLY this JSON shape, nothing else:
            {
              "name": "string or null",
              "objective": "string or null",
              "channel": "Email or SMS or null"
            }
            """;

            public static string ExtractionUser(string message) =>
                $"User message: \"{message}\"";

            /// <summary>
            /// Generates the next clarifying question based on what is still missing.
            /// Always asks ONE question — the most important missing field.
            /// </summary>
            public const string NextQuestionSystem = """
    You are a friendly CRM campaign creation assistant.
    The user is creating a marketing campaign.
    Ask ONE short, friendly question to collect the most important missing field.

    PRIORITY ORDER (ask in this order if missing):
    1. channel — "Will this be an Email campaign or SMS?"
    2. objective — Ask based on what we already know:
       - If we have a name like "Eid Campaign" → reference it: 
         "What is the main goal of your Eid campaign — promoting offers, 
          rewarding loyal customers, or something else?"
       - If no name → "What is the main goal of this campaign?"
    3. name:
       - Suggest 2 names derived from the channel + objective already collected.
       - Example: if channel=SMS, objective="Re-engage lapsed customers" →
         suggest "Win-Back SMS" and "Lapsed Customer Re-engagement"
       - Ask: "What would you like to call this campaign? 
         Here are two ideas based on your goal: [name1] or [name2] — 
         or type your own."

    RULES:
    - Ask only ONE question
    - Maximum 2 sentences
    - Be warm and conversational
    - When asking for name: ALWAYS include suggestions in the JSON field below

    Return ONLY a JSON object:
    {
      "question": "Your question here.",
      "suggestions": ["Suggested Name 1", "Suggested Name 2"]
    }
    Omit "suggestions" (or set to []) when asking for channel or objective.
    """;

            public static string NextQuestionUser(
                CampaignDraftDto draft, List<string> missing) =>
                $"Already collected:\n" +
                $"- Name: {draft.Name ?? "not yet provided"}\n" +
                $"- Objective: {draft.Objective ?? "not yet provided"}\n" +
                $"- Channel: {draft.Channel ?? "not yet provided"}\n\n" +
                $"Still missing: {string.Join(", ", missing)}\n\n" +
                $"Ask for the most important missing field:";

            /// <summary>
            /// Builds a confirmation summary before saving.
            /// </summary>
            public static string ConfirmationMessage(CampaignDraftDto draft) =>
                $"Here's what I have for your campaign:\n\n" +
                $"📋 **Name:** {draft.Name}\n" +
                $"🎯 **Objective:** {draft.Objective}\n" +
                $"📣 **Channel:** {draft.Channel}\n" +
                (draft.SelectionName != null
                    ? $"👥 **Audience:** {draft.SelectionName}\n"
                    : "") +
                $"\nShall I create this campaign?";
        }
    }
}