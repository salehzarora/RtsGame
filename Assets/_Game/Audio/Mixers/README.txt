Mixers folder
=============

Optional. The audio system currently mixes via three code-driven volume buses
on the AudioManager (Master / SFX / Music), persisted to PlayerPrefs — no
AudioMixer asset is required, and nothing breaks without one.

If you later want an AudioMixer (for ducking, reverb snapshots, or exposed
parameters), create it here (Assets → Create → Audio Mixer) and route the
AudioManager's pooled/music/ambience AudioSources through its groups. The
current setup intentionally avoids a hard AudioMixer dependency so the game
runs out of the box.
