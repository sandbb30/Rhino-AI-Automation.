#! python 3
"""Generate a layered, conceptual twisted tower in Rhino 8."""

import math
import System
from System.Drawing import Color
import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc


FLOOR_COUNT = 20
FLOOR_HEIGHT = 3.6
WIDTH = 24.0
DEPTH = 18.0
ROTATION_PER_FLOOR = 3.0  # Degrees between successive floor plates.


def rectangle(z, angle_degrees, width, depth):
    """Return a closed rectangle rotated around the world Z axis."""
    points = [Rhino.Geometry.Point3d(x, y, z) for x, y in (
        (-width / 2, -depth / 2), (width / 2, -depth / 2),
        (width / 2, depth / 2), (-width / 2, depth / 2),
        (-width / 2, -depth / 2))]
    curve = Rhino.Geometry.PolylineCurve(points)
    curve.Transform(Rhino.Geometry.Transform.Rotation(
        math.radians(angle_degrees), Rhino.Geometry.Vector3d.ZAxis,
        Rhino.Geometry.Point3d.Origin))
    return curve


def add_geometry(geometry, layer_index, name):
    attributes = Rhino.DocObjects.ObjectAttributes()
    attributes.LayerIndex = layer_index
    attributes.Name = name
    object_id = sc.doc.Objects.Add(geometry, attributes)
    if object_id == System.Guid.Empty:
        raise RuntimeError("Could not add {}".format(name))
    return object_id


def main():
    floor_count = rs.GetInteger("Number of floors", FLOOR_COUNT, 1, 500)
    if floor_count is None:
        return
    floor_height = rs.GetReal("Floor height (document units)", FLOOR_HEIGHT, 0.001)
    if floor_height is None:
        return
    width = rs.GetReal("Tower width (document units)", WIDTH, 0.001)
    if width is None:
        return
    depth = rs.GetReal("Tower depth (document units)", DEPTH, 0.001)
    if depth is None:
        return
    rotation = rs.GetReal("Rotation per floor (degrees)", ROTATION_PER_FLOOR)
    if rotation is None:
        return

    # Prepare geometry before modifying the document. There are N occupied
    # floor plates and a roof at N * floor_height, enclosing N full storeys.
    tolerance = sc.doc.ModelAbsoluteTolerance
    if min(width, depth, floor_height) <= 10 * tolerance:
        raise ValueError("Dimensions must exceed 10 times the document tolerance.")
    profiles = [rectangle(i * floor_height, i * rotation, width, depth)
                for i in range(floor_count + 1)]
    plates = []
    for profile in profiles:
        breps = Rhino.Geometry.Brep.CreatePlanarBreps(profile, tolerance)
        if not breps or len(breps) != 1:
            raise RuntimeError("Failed to create a floor plate.")
        plates.append(breps[0])

    # Separate ruled panels preserve corner correspondence through large twists.
    panels = []
    for level in range(floor_count):
        lower = profiles[level].ToPolyline()
        upper = profiles[level + 1].ToPolyline()
        for corner in range(4):
            panel = Rhino.Geometry.Brep.CreateFromCornerPoints(
                lower[corner], lower[corner + 1],
                upper[corner + 1], upper[corner], tolerance)
            if panel is None:
                raise RuntimeError("Failed to create a facade panel.")
            panels.append((level, corner, panel))

    root_name = "Twisted Tower"
    suffix = 2
    while sc.doc.Layers.FindByFullPath(root_name, -1) >= 0:
        root_name = "Twisted Tower {:02d}".format(suffix)
        suffix += 1

    undo = sc.doc.BeginUndoRecord("Generate twisted tower")
    created = []
    layer_indices = []
    rs.EnableRedraw(False)
    try:
        root = Rhino.DocObjects.Layer()
        root.Name = root_name
        root_index = sc.doc.Layers.Add(root)
        if root_index < 0:
            raise RuntimeError("Could not create tower layer.")
        layer_indices.append(root_index)
        parent_id = sc.doc.Layers[root_index].Id
        layers = {}
        for name, color in (("Floor Outlines", Color.DarkSlateGray),
                            ("Floor Plates", Color.LightSteelBlue),
                            ("Roof", Color.SlateBlue),
                            ("Facade", Color.LightCyan)):
            layer = Rhino.DocObjects.Layer()
            layer.Name = name
            layer.ParentLayerId = parent_id
            layer.Color = color
            index = sc.doc.Layers.Add(layer)
            if index < 0:
                raise RuntimeError("Could not create {} layer.".format(name))
            layer_indices.append(index)
            layers[name] = index

        for level, profile in enumerate(profiles):
            label = "Roof" if level == floor_count else "Floor {:02d}".format(level + 1)
            created.append(add_geometry(profile, layers["Floor Outlines"], label + " outline"))
            layer = layers["Roof" if level == floor_count else "Floor Plates"]
            created.append(add_geometry(plates[level], layer, label + " surface"))
        for level, corner, panel in panels:
            created.append(add_geometry(panel, layers["Facade"],
                                        "Storey {:02d} panel {}".format(level + 1, corner + 1)))
    except Exception:
        for object_id in created:
            sc.doc.Objects.Delete(object_id, True)
        for index in reversed(layer_indices):
            sc.doc.Layers.Delete(index, True)
        raise
    finally:
        sc.doc.EndUndoRecord(undo)
        rs.EnableRedraw(True)
        sc.doc.Views.Redraw()

    print("Created {} floors; height {:.3f}; roof twist {:.3f} degrees. Layer: {}".format(
        floor_count, floor_count * floor_height, floor_count * rotation, root_name))


if __name__ == "__main__":
    main()
