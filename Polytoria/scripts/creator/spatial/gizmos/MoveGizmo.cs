// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Utils;
using System;
using System.Collections.Generic;

namespace Polytoria.Creator.Spatial;

public partial class MoveGizmo : Node, IGizmo
{
    private const float GizmoArrowStart = 0.25f;
	private const float GizmoArrowLength = 1.0f;
    private const float GizmoArrowEnd = GizmoArrowStart + GizmoArrowLength;

	private const float ShaftRadius = 0.025f;
    private const float ConeRadius = 0.10f;
	private const float HoverThickness = 2.0f;
	private const float SelectionMultiplier = 5f;
	
	private Vector3 _ivec = new(0f, 0f, -1f);
	private Vector3 _nivec = new(-1f, -1f, 0f);

	public List<Dynamic> Targets { get; set; } = [];
	public bool Visible { get; set; }
	public Gizmos? RootGizmos { get; set; }
	private ArrayMesh[] _moveGizmo = new ArrayMesh[6];
	private ArrayMesh[] _moveGizmoHover = new ArrayMesh[6];
	private MeshInstance3D[] _moveGizmoInstance = new MeshInstance3D[6];
	private Camera3D GDCamera => RootGizmos!.Root.Environment.CurrentGDCamera!;
	private MoveGizmoAxis _currentAxis = MoveGizmoAxis.None;
	private StandardMaterial3D[] _gizmoColor = new StandardMaterial3D[6];
	private StandardMaterial3D[] _gizmoHoverColor = new StandardMaterial3D[6];
	private bool _isMouseDragging;
	private Vector3? _startRayOrigin;
	private Vector3? _startRayNormal;
	private Transform3D _dragPivot;

	public event Action? DragStarted;
	public event Action? DragEnded;
	public event Action<Vector3>? Dragged;

	public bool IsLocalSpace { get; set; } = false;

	public enum MoveGizmoAxis
	{
		None = -1,
        MoveXPos = 0,
        MoveXNeg = 1,
        MoveYPos = 2,
        MoveYNeg = 3,
        MoveZPos = 4,
        MoveZNeg = 5,
	}

	private Transform3D GetPivot()
	{
		Transform3D center = Gizmos.GetCenterPivot([.. Targets]);

		if (IsLocalSpace && Targets.Count == 1)
		{
			Basis localBasis = Targets[0].GetGlobalTransform().Basis.Orthonormalized();
			return new Transform3D(localBasis, center.Origin);
		}

		return new Transform3D(Basis.Identity, center.Origin);
	}

	private static int GetBaseAxis(MoveGizmoAxis axis)
    {
        return axis switch
        {
            MoveGizmoAxis.MoveXPos or MoveGizmoAxis.MoveXNeg => 0,
            MoveGizmoAxis.MoveYPos or MoveGizmoAxis.MoveYNeg => 1,
            MoveGizmoAxis.MoveZPos or MoveGizmoAxis.MoveZNeg => 2,
            _ => -1
        };
    }

	private static float GetAxisDirection(MoveGizmoAxis axis)
    {
        return axis switch
        {
            MoveGizmoAxis.MoveXPos or MoveGizmoAxis.MoveYPos or MoveGizmoAxis.MoveZPos => 1f,
            MoveGizmoAxis.MoveXNeg or MoveGizmoAxis.MoveYNeg or MoveGizmoAxis.MoveZNeg => -1f,
            _ => 0f
        };
    }

	public override void _EnterTree()
	{
		CreateSurfTool();
		CreateInstances();
	}

	public override void _ExitTree()
	{
		ClearInstances();
	}

	private void CreateSurfTool()
	{
		for (int i = 0; i < 6; i++)
		{
			int axisIndex = i / 2;
			Color axisColor = Gizmos.AxisColors[axisIndex];
			Color axisHoverColor = Color.FromHsv(axisColor.H, 0.25f, 1f);

			StandardMaterial3D material = new()
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				RenderPriority = (int)Godot.Material.RenderPriorityMax,
				NoDepthTest = true,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				AlbedoColor = axisColor
			};

			StandardMaterial3D materialHover = (StandardMaterial3D)material.Duplicate();
			materialHover.AlbedoColor = axisHoverColor;

			_gizmoColor[i] = material;
			_gizmoHoverColor[i] = materialHover;

			_moveGizmo[i] = new ArrayMesh();
			BuildArrowMesh(_moveGizmo[i], material, ShaftRadius, ConeRadius);

			_moveGizmoHover[i] = new ArrayMesh();
			BuildArrowMesh(
				_moveGizmoHover[i], materialHover, 
				ShaftRadius * HoverThickness, 
				ConeRadius * HoverThickness
			);
		}
	}

	private void BuildArrowMesh(ArrayMesh mesh, StandardMaterial3D material, float shaftRadius, float coneRadius)
    {
        SurfaceTool surftool = new();
        surftool.Begin(Godot.Mesh.PrimitiveType.Triangles);

        float coneGrowth = coneRadius - ConeRadius;
		float coneBaseOffset = Gizmos.GizmoArrowSize + coneGrowth;
		float coneTipOffset  = GizmoArrowEnd + coneGrowth;
		float shaftEnd = coneTipOffset - coneBaseOffset;

        Vector3[] arrow = [
			Vector3.Zero * shaftRadius + _ivec * GizmoArrowStart,			// shaft base inner (on axis)
  			_nivec.Normalized() * shaftRadius + _ivec * GizmoArrowStart,	// shaft base outer
			_nivec.Normalized() * shaftRadius + _ivec * shaftEnd,			// shaft top outer
			_nivec.Normalized() * coneRadius  + _ivec * shaftEnd,			// cone base outer
			Vector3.Zero + _ivec * coneTipOffset,							// cone tip (on axis)
        ];

        int arrowPoints = arrow.Length;
        int arrowSides = 16;
        float arrowSidesStep = Mathf.Tau / arrowSides;

        for (int k = 0; k < arrowSides; k++)
        {
            Basis ma = new(_ivec, k * arrowSidesStep);
            Basis mb = new(_ivec, (k + 1) * arrowSidesStep);

            for (int j = 0; j < arrowPoints - 1; j++)
            {
                Vector3[] points = [
                    ma.Xform(arrow[j]),
                    mb.Xform(arrow[j]),
                    mb.Xform(arrow[j + 1]),
                    ma.Xform(arrow[j + 1]),
                ];

                surftool.AddVertex(points[0]);
                surftool.AddVertex(points[1]);
                surftool.AddVertex(points[2]);

                surftool.AddVertex(points[0]);
                surftool.AddVertex(points[2]);
                surftool.AddVertex(points[3]);
            }
        }

        surftool.SetMaterial(material);
        surftool.Commit(mesh);
    }

	private void CreateInstances()
	{
		for (int i = 0; i < 6; i++)
		{
			_moveGizmoInstance[i] = new MeshInstance3D
			{
				Mesh = _moveGizmo[i],
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Visible = false,
				// not using 1 because of decal wrapping onto gizmos
				Layers = 1 << 6
			};
			AddChild(_moveGizmoInstance[i]);
		}
	}

	private void ClearInstances()
	{
		for (int i = 0; i < 6; i++)
		{
			_moveGizmoInstance[i].QueueFree();
		}
	}

	public override void _Process(double delta)
	{
		SetVisiblity();
		RedrawGizmo();
	}

	public override void _Input(InputEvent @event)
	{
		if (Targets.Count == 0) return;

		Vector2 mousePos = GDCamera.GetViewport().GetMousePosition();
		Vector3 rayOrigin = GDCamera.ProjectRayOrigin(mousePos);
		Vector3 rayNormal = GDCamera.ProjectRayNormal(mousePos);
		Vector3 cameraNormal = -GDCamera.GlobalBasis.Column2;

		if (@event is InputEventMouseButton btn)
		{
			if (btn.ButtonIndex != MouseButton.Left) return;
			if (btn.Pressed)
			{
				if (_currentAxis == MoveGizmoAxis.None) return;
				if (!Visible) return;

				_dragPivot = GetPivot();
				_startRayOrigin = rayOrigin;
				_startRayNormal = rayNormal;
				DragStarted?.Invoke();
				_isMouseDragging = true;
			}
			else
			{
				if (_isMouseDragging)
				{
					DragEnded?.Invoke();
					_isMouseDragging = false;
				}
			}
		}
		else if (@event is InputEventMouseMotion)
		{
			if (!Visible) return;
			if (_isMouseDragging)
			{
				if (_currentAxis != MoveGizmoAxis.None)
				{
					DragTransform(rayOrigin, rayNormal, cameraNormal);
				}
			}
			else
			{
				UpdateAxis(rayOrigin, rayNormal, cameraNormal);
			}
		}
		base._Input(@event);
	}

	private void RedrawGizmo()
	{
		if (Targets.Count == 0) return;
		if (!Visible) return;

		Transform3D pform = GetPivot();
		float gizmoScale = pform.Origin.DistanceTo(GDCamera.GlobalPosition) * 0.12f;
		Vector3 pScale = new(gizmoScale, gizmoScale, gizmoScale);

		for (int i = 0; i < 6; i++)
		{
			int axisIndex = i / 2;
			bool isNegative = (i % 2) == 1;
			bool isHovered = (int)_currentAxis == i;

			Vector3 axisDir = pform.Basis.GetColumn(axisIndex).Normalized();
			Vector3 lookDir = isNegative ? -axisDir : axisDir;
			Vector3 upVec = GetSafeUpVector(axisDir);

			Transform3D axisTransform = new();

			if (lookDir.LengthSquared() > 1e-6f && Mathf.Abs(lookDir.Dot(upVec)) < 0.9999f)
			{
				axisTransform = axisTransform.LookingAt(lookDir, upVec);
			}

			axisTransform.Basis = axisTransform.Basis.Scaled(pScale);
			axisTransform.Origin = pform.Origin;

			_moveGizmoInstance[i].Transform = axisTransform;
			_moveGizmoInstance[i].Mesh = isHovered ? _moveGizmoHover[i] : _moveGizmo[i];
		}
	}

	private static Vector3 GetSafeUpVector(Vector3 dir)
	{
		// If direction is mostly along Y, use Z as up, otherwise use Y
		if (Mathf.Abs(dir.Dot(Vector3.Up)) > 0.9f)
			return Vector3.Back;
		return Vector3.Up;
	}

	private void SetVisiblity()
	{
		for (int i = 0; i < 6; i++)
		{
			_moveGizmoInstance[i].Visible = Visible;
		}
	}

	private void UpdateAxis(Vector3 rayOrigin, Vector3 rayNormal, Vector3 cameraNormal)
	{
		if (Targets.Count == 0) return;

		Transform3D pivot = GetPivot();
		float gizmoScale = pivot.Origin.DistanceTo(GDCamera.GlobalPosition) * 0.12f;

		float bestDist = float.MaxValue;
		int bestArrow = -1;

		for (int i = 0; i < 6; i++)
		{
			int axisIndex = i / 2;
			bool isNegative = (i % 2) == 1;

			Vector3 axisDir = pivot.Basis.GetColumn(axisIndex).Normalized();
			if (isNegative) axisDir = -axisDir;

			Vector3 arrowBase = pivot.Origin + axisDir * (gizmoScale * GizmoArrowStart);
			Vector3 coneBase  = pivot.Origin + axisDir * (gizmoScale * (GizmoArrowEnd - Gizmos.GizmoArrowSize));
			Vector3 arrowTip  = pivot.Origin + axisDir * (gizmoScale * GizmoArrowEnd);

			float shaftPickRadius = ShaftRadius * gizmoScale * SelectionMultiplier;
			float conePickRadius  = ConeRadius * gizmoScale * SelectionMultiplier;

			float? shaftHit = RayVsCylinder(
				rayOrigin, rayNormal,
				arrowBase, coneBase,
				shaftPickRadius
			);

			float? coneHit = RayVsCone(
				rayOrigin, rayNormal,
				coneBase, arrowTip,
				conePickRadius
			);

			float hitDist = float.MaxValue;
			if (shaftHit.HasValue) hitDist = Mathf.Min(hitDist, shaftHit.Value);
			if (coneHit.HasValue)  hitDist = Mathf.Min(hitDist, coneHit.Value);

			if (hitDist < bestDist)
			{
				bestDist = hitDist;
				bestArrow = i;
			}
		}

		if (bestDist == float.MaxValue)
			bestArrow = -1;

		HighlightAxis(bestArrow);
	}

	private static float? RayVsCylinder(Vector3 rayOrigin, Vector3 rayDir, Vector3 cylStart, Vector3 cylEnd, float radius)
	{
		Vector3 cylAxis = cylEnd - cylStart;
		float cylLen = cylAxis.Length();
		if (cylLen < 1e-6f) return null;

		Vector3 cylDir = cylAxis / cylLen;

		Vector3 oc = rayOrigin - cylStart;

		Vector3 rayPerp = rayDir - rayDir.Dot(cylDir) * cylDir;
		Vector3 ocPerp  = oc - oc.Dot(cylDir) * cylDir;

		float a = rayPerp.Dot(rayPerp);
		float b = 2f * rayPerp.Dot(ocPerp);
		float c = ocPerp.Dot(ocPerp) - radius * radius;

		if (Mathf.Abs(a) < 1e-8f) return null;

		float discriminant = b * b - 4f * a * c;
		if (discriminant < 0f) return null;

		float sqrtDisc = Mathf.Sqrt(discriminant);
		float t0 = (-b - sqrtDisc) / (2f * a);
		float t1 = (-b + sqrtDisc) / (2f * a);

		foreach (float t in new[] { t0, t1 })
		{
			if (t < 0f) continue;

			Vector3 hitPoint = rayOrigin + rayDir * t;
			float proj = (hitPoint - cylStart).Dot(cylDir);

			if (proj >= 0f && proj <= cylLen)
				return t;
		}

		return null;
	}

	private static float? RayVsCone(Vector3 rayOrigin, Vector3 rayDir, Vector3 baseCenter, Vector3 tip, float baseRadius)
	{
		Vector3 axis = baseCenter - tip;
		float height = axis.Length();
		if (height < 1e-6f) return null;

		Vector3 axisDir = axis / height;

		float cosAngle = height  / Mathf.Sqrt(baseRadius * baseRadius + height * height);
		float cos2 = cosAngle * cosAngle;

		Vector3 oc = rayOrigin - tip;

		float dDotA  = rayDir.Dot(axisDir);
		float ocDotA = oc.Dot(axisDir);

		float a = dDotA * dDotA - cos2;
		float b = 2f * (dDotA * ocDotA - rayDir.Dot(oc) * cos2);
		float c = ocDotA * ocDotA - oc.Dot(oc) * cos2;

		if (Mathf.Abs(a) < 1e-8f) return null;

		float discriminant = b * b - 4f * a * c;
		if (discriminant < 0f) return null;

		float sqrtDisc = Mathf.Sqrt(discriminant);
		float t0 = (-b - sqrtDisc) / (2f * a);
		float t1 = (-b + sqrtDisc) / (2f * a);

		float? result = null;

		foreach (float t in new[] { t0, t1 })
		{
			if (t < 0f) continue;

			Vector3 hitPoint = rayOrigin + rayDir * t;
			float proj = (hitPoint - tip).Dot(axisDir);

			if (proj < 0f || proj > height) continue;

			if (!result.HasValue || t < result.Value)
				result = t;
		}

		return result;
	}

	private void HighlightAxis(int axis)
	{
		for (int i = 0; i < 6; i++)
		{
			_moveGizmo[i].SurfaceSetMaterial(0, i == axis ? _gizmoHoverColor[i] : _gizmoColor[i]);
		}

		_currentAxis = (MoveGizmoAxis)axis;
		if (RootGizmos != null)
		{
			if (_currentAxis != MoveGizmoAxis.None)
			{
				RootGizmos.HoveringGizmos = true;
			}
			else
			{
				RootGizmos.HoveringGizmos = false;
			}
		}
	}

	private void DragTransform(Vector3 rayOrigin, Vector3 rayNormal, Vector3 cameraNormal)
	{
		int baseAxis = GetBaseAxis(_currentAxis);
		float direction = GetAxisDirection(_currentAxis);

		Vector3 axisDir = _dragPivot.Basis.GetColumn(baseAxis).Normalized() * direction;

		Vector3 planeNormal = axisDir.Cross(axisDir.Cross(cameraNormal)).Normalized();
		Plane plane = new(planeNormal, _dragPivot.Origin);

		Vector3? intersection = plane.IntersectsRay(rayOrigin, rayNormal);
		Vector3? click        = plane.IntersectsRay(_startRayOrigin!.Value, _startRayNormal!.Value);

		if (intersection == null || click == null) return;

		Vector3 motion = axisDir * axisDir.Dot(intersection.Value - click.Value);

		Dragged?.Invoke(motion);
	}
}
