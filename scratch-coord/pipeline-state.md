# Pipeline state — updated by coordinator
Tip: 42905f5 (card-coverage-parallel). Fast gate green (2,642 Rules tests).
Slow gate: running at 42905f5 in .claude/worktrees/r21-a (task bmspuw111, started ~this session; ~1h45m; output scratchpad/gate-42905f5.txt in that worktree).

## Slots (branch -> topic)
r21-61 group attack bans (+9)      | resumed x3, 503-prone
r21-62 trigger probe battery       | resumed x3, 503-prone (verification)
r21-63 soak non-resolution 2151    | resumed x2 (verification)
r21-64 colour+subtype addition +9  | running
r21-65 is every creature type +6   | died x3, in resume queue
r21-66 quoted-ability comma +6     | resumed (slot 4 of drip)
r21-67 Assault Suit conjuncts      | died, in resume queue
r21-68 damage-combat grind         | running
r21-69 card movement grind         | running
r21-70 mutation sweep r21-52..59   | running (verification)

## Resume drip (user directive: exponential backoff + jitter, one at a time)
Queue after timer bzsteimdp (168s): r21-67 -> r21-65 -> r21-61 -> r21-62 (delays ~240s, ~300s, ~360s jittered; recheck ListAgents before each — skip any already running)

## On merge-on-arrival (pipeline shape, user-mandated)
Per agent report: git merge <branch> --no-edit; fast gate (dotnet build; dotnet test tests/MtgEngine.Rules.Tests --no-build); launch replacement into freed slot IMMEDIATELY but STAGGERED per backoff policy; never batch.
