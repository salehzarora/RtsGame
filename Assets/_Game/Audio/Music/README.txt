Music folder
============

Drop your long, looping music tracks here, then assign them directly on the
AudioManager component (on the "AudioManager" GameObject in the scene):

  • Menu Music     → played on the main menu and between matches.
  • Gameplay Music → crossfades in when a match starts.

The AudioManager crossfades between the two automatically (driven by
GameStateManager.OnGameStarted / OnGameReset) and never double-stacks the same
track when you return to the menu or restart a match.

Import tip: set these clips' Load Type to "Streaming" (or Compressed In Memory)
in the AudioClip import settings so a long track doesn't sit decompressed in RAM.

LICENSING: royalty-free / CC0 / properly-licensed only. No copyrighted music.
