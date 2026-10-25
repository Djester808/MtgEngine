# MTGO rules-engine investigation

Started 2026-10-21. Objective: understand observable MTGO behavior and architecture to inform an independently implemented engine in this repository.

Follow-up: [Action/state boundary and Card.dll findings](ACTION_STATE_AND_CARD_FINDINGS.md) records the completed next investigation pass. Its findings supersede initial hypotheses below where indicated, including the initial statement that C# had not been reconstructed.

## Acquisition and identity

- No installed client found in the searched locations: Local AppData ClickOnce Apps/2.0, Program Files, Program Files (x86), Local AppData filename inventory, Downloads, per-user/system Start Menu shortcuts, and standard HKCU/HKLM uninstall records. This is a bounded search, not proof that no copy exists anywhere.
- Official entry point: https://www.mtgo.com/getting-started
- Official installer: https://mtgo.patch.daybreakgames.com/patch/mtg/live/client/setup.exe?v=23
- Saved installer: artifacts/setup.exe, 563320 bytes; SHA-256 B02B503F85B081C3A2332163744DD3124C49D0F21A9E75FDA893DD4F27313B6C. Windows Authenticode reported Valid, signer Daybreak Game Company LLC.
- Installer strings identify a ClickOnce deployment named MTGO.application. Retrieved its manifest over HTTPS and followed its declared application-manifest path.
- Deployment manifest: https://mtgo.patch.daybreakgames.com/patch/mtg/live/client/MTGO.application
- Selected application manifest: https://mtgo.patch.daybreakgames.com/patch/mtg/live/client/3.4.159.4708.20261014120111/MTGO.exe.manifest
- Version observed: 3.4.159.4708. This is the version served during acquisition; subsequent releases may change the deployment.
- Retrieved exactly four selected DLLs using manifest codebase names under that version directory: MTGOEnumStruct.dll, MTGOMessage.dll, WotC.MtGO.Client.Model.Play.dll, WotC.MTGO.Common.GameServer.Attribute.dll. The server uses direct names; attempted .deploy suffix URLs returned 404.
- No installer or target application was executed. No login, live traffic acquisition, or server interaction beyond public artifact downloads occurred. Downloaded binaries are excluded by this directory's .gitignore.

## Evidence and initial findings

REA CLI 6.7.0 supplied the managed static inspection fallback. Its MCP server is absent from the current session; doctor reported missing Codex registration. No client configuration was changed. The installed skill describes some newer capabilities, so only commands advertised by this CLI were used.

1. **Observed:** gameplay assembly is AnyCPU CIL targeting .NET Framework 4.7.2, version 3.4.159.4708. Its metadata contains 483 type definitions and 4494 method definitions. SHA-256: 7b16823fbaa9a965de3b9ee69ba727992a9b7b50d1187defadd9ac5a6c688081.
   Source: play-evidence.json, Evidence ev_afdf0063e948620c4708651e233186a75f90260906136f45368175471ad01ff4.
2. **Observed:** declared types include GameCard, GameCardZone, GamePlayer, GamePrompt, ManaPool, Actions.GameAction, Actions.TargetSet, and InProgressGameEvent.Game. GamePlayer declares HasPriority accessors. GamePrompt declares Options, PromptedPlayer, TurnStepFlags, Create, and AddAction members.
   Source: play-members-evidence.json, Evidence ev_5c508a6e3992121057eb96955fb197522fc3ed304c4de84eacaa75354286b265; smaller selection in gameplay-summary.json.
3. **Observed:** Game declares ExecuteAction, Process, HandleGamePlayStatusMessageDetailed, ProcessPlayerStatusElement, ProcessThingElement, HandleCardSelectorElement, ProcessInteractStateElement, and handlers for commander/dungeon information. These names describe declared members; their names alone do not prove runtime semantics.
   Source: same member Evidence.
4. **Observed:** decoded CIL call edges from GameCard.ResolveCardActionsHelper refer to Game.get_Prompt, DigitalMagicObject.get_Properties, PropertyContainer.TryGetSubList, and integer, boolean, and string property readers. This establishes static call relationships, not execution or the source of those property values.
   Source: same member Evidence, call_edges collection.
5. **Inference:** a property-driven game-state and interaction model is a promising way to study the client. Server-authoritative rule evaluation is a working hypothesis requiring further tracing, not an established conclusion. No recovered layer evaluator, replacement-effect scheduler, or full card-rule interpreter has been established in this pass.
6. **Observed inventory only:** the manifest lists Card.dll (226174976 bytes), Mtgo.Shared.Parser.Attributes.dll, MTGOEnumStruct.dll and MTGOMessage.dll. Card.dll has not been downloaded. Its name and size do not establish executable card rules or distinguish resources from logic.

## Relevance to this project

The working tree contains MtgEngine.Rules source for casting, layers, state-based actions, triggers, replacements, turns, zones, mana, and views. Existing uncommitted work was left untouched. README/CLAUDE descriptions say a former engine was removed, so those descriptions should not be used to infer the status of the currently present engine sources.

Use MTGO evidence to derive behavioral requirements and independently authored tests. Retain the published Comprehensive Rules as the normative basis. Do not treat client property names or UI resolution helpers as a complete rules implementation.

## Next questions

- Trace Game.ExecuteAction through its outgoing message calls, and trace incoming gameplay messages into property updates. This should sharpen the client/server responsibility boundary.
- Inspect downloaded message and enum assemblies for prompt, action, zone, turn-step and object-identity schemas, with exact Evidence references.
- Inspect Card.dll's resource/code inventory before assuming it contains a card interpreter.
- Determine where legal targets and offered actions originate, and whether any local validators implement gameplay rather than deck legality or UI checks.
- Map confirmed behavior to existing engine seams and identify independent regression scenarios for priority, casting/payment, target changes, triggers, replacement effects and simultaneous state-based actions.

## Limits

Static metadata and CIL anchors were inspected; C# was not decompiled and application behavior was not observed at runtime. Assembly dependencies were not loaded or resolved by executing the target. Metadata tokens are meaningful only with the exact artifact hash and MVID in the Evidence. Server binaries and server-side card implementations remain unavailable. This is an initial investigation, not a reconstruction of MTGO's rules engine.
