# Rhino AI Automation

This project uses OpenAI Codex to develop automation tools for Rhino 8.

## Features
- Rhino 8 Python scripting
- Grasshopper parametric design
- AI-assisted 3D modelling
- Rhino MCP integration
- Architectural and MEP automation

## Goal
Create AI-powered tools to automate complex 3D modelling and design workflows.

## Twisted tower generator (Rhino 8)

Run [`scripts/twisted_tower.py`](scripts/twisted_tower.py) in Rhino 8's built-in
Python 3 editor. No external Python packages or API keys are required.

1. Download or clone this repository onto the computer running Rhino 8.
2. Open a Rhino model and choose its units before running the script. For the
   default dimensions, use **meters** (File > Properties > Units).
3. Enter `ScriptEditor` in Rhino's command line. Open `scripts/twisted_tower.py`
   as a **Python 3** script and click **Run**.
4. Accept or adjust each prompt. Press Escape to cancel before geometry is added.
5. Run `Zoom` > `Extents` and use a Shaded or Rendered viewport to inspect it.

| Parameter | Default | Meaning |
| --- | --- | --- |
| Number of floors | 20 | Occupied floors; adjustable from 1 to 500 |
| Floor height | 3.6 | Distance between levels, in document units |
| Width | 24 | Rectangular floor width, in document units |
| Depth | 18 | Rectangular floor depth, in document units |
| Rotation per floor | 3° | Increment around the world Z axis; negative reverses twist |

You can also edit the constants at the top of the script to change prompt defaults.
The tower is centered at the world origin, with its first floor at Z = 0. Twenty
storeys at the defaults reach a roof height of 72 units and a roof rotation of
60°. There are 20 occupied floor surfaces plus a separate roof surface at level 20.
For a desired total roof twist, divide that angle by the number of floors.

Each run creates a new `Twisted Tower` parent layer (with a numbered suffix on
subsequent runs) containing these sublayers:

- **Floor Outlines**: closed curves at all floor and roof levels.
- **Floor Plates**: planar surfaces for the occupied floors.
- **Roof**: the top planar surface.
- **Facade**: four surface panels per storey, connecting corresponding corners.

Objects are named by floor or storey. Hide the Facade layer to inspect the floor
plates. One Rhino Undo removes a successful generation; existing model geometry
is preserved. Layer colors are assigned to geometry through the default By Layer
object color setting.

This is conceptual geometry: floor plates have no thickness and facade panels
are separate surfaces, not a joined watertight solid. Large rotations can produce
intersecting or impractical panels. It does not generate structure, a core, or
construction details. Rhino/Grasshopper are not required to edit the file, but
Rhino 8 is required to execute and visually validate it.
