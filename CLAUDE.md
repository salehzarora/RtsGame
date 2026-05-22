# RTS Prototype Rules

We are building a small singleplayer 3D RTS prototype in Unity 6 using C# and URP.

The game is inspired by classic base-building RTS games, but it must be original. Do not copy names, assets, factions, UI, or exact mechanics from any copyrighted game.

Core rules:
- Keep code modular.
- Do not create huge scripts.
- One responsibility per script.
- Do not rename existing public fields unless necessary.
- Do not delete existing files without asking.
- Every new script must include clear setup instructions in Unity Inspector.
- Prefer simple MonoBehaviour architecture first.
- Do not use DOTS/ECS unless explicitly requested.
- Do not add multiplayer.
- Do not add external packages unless explicitly requested.
- After every change, explain:
  1. What files changed
  2. What was added
  3. How to test it in Unity
  4. What can break

Current prototype goal:
- RTS camera
- Unit selection
- Right-click movement
- Basic combat
- Resource gathering
- Building placement
- Unit production