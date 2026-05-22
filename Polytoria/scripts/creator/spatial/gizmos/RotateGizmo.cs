// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Utils;
using System;
using System.Collections.Generic;

namespace Polytoria.Creator.Spatial;

public partial class RotateGizmo : Node, IGizmo
{
	private const float GizmoRingHalfWidth = 0.1f;
	private Vector3 _ivec = new(0f, 0f, -1f);
	private Vector3 _ivec2 = new(-1f, 0f, 0f);
	private Vector3? _lastIntersection;
	private Shader _rotateShader = GD.Load<Shader>("res://resources/shaders/gizmos/rotate.gdshader");
	private Shader _rotateBorderShader = GD.Load<Shader>("res://resources/shaders/gizmos/rotate_border.gdshader");

	public List<Dynamic> Targets { get; set; } = [];
	public bool Visible { get; set; }
	public Gizmos? RootGizmos { get; set; }

	private ArrayMesh[] _rotateGizmo = new ArrayMesh[4];
	private MeshInstance3D[] _rotateGizmoInstance = new MeshInstance3D[4];

	private MeshInstance3D _arcInstance = new();
	private ImmediateMesh _arcMesh = new();
	private ShaderMaterial _arcMaterial = new();

	private Vector3 _arcStartDirection;
	private float _totalRotationAngle;

	private Camera3D GDCamera => RootGizmos!.Root.Environment.CurrentGDCamera!;
	private RotateGizmoAxis _currentAxis = RotateGizmoAxis.None;

	private ShaderMaterial[] _rotateColor = new ShaderMaterial[3];
	private ShaderMaterial[] _rotateHoverColor = new ShaderMaterial[3];

	private bool _isMouseDragging;
	private Vector3? _startRayOrigin;
	private Vector3? _startRayNormal;
	private float _gizmoScale;

	public event Action? DragStarted;
	public event Action? DragEnded;
	public event Action<Basis>? Dragged;

	private Transform3D _dragPivot;
	public bool IsLocalSpace { get; set; } = false;

	public enum RotateGizmoAxis
	{
		None = -1,
		RotateX,
		RotateY,
		RotateZ
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
		for (int i = 0; i < 3; i++)
		{
			Color axisColor = Gizmos.AxisColors[i];
			Color axisHoverColor = Color.FromHsv(axisColor.H, 0.25f, 1f);

			_rotateGizmo[i] = new();

			ShaderMaterial rotateMaterial = new()
			{
				Shader = _rotateShader,
				RenderPriority = 5
			};
			rotateMaterial.SetShaderParameter("albedo", axisColor);

			_rotateColor[i] = rotateMaterial;

			ShaderMaterial rotateHoverMaterial = new()
			{
				Shader = _rotateShader,
				RenderPriority = 5
			};
			rotateHoverMaterial.SetShaderParameter("albedo", axisHoverColor);

			_rotateHoverColor[i] = rotateHoverMaterial;

			SurfaceTool surftool = new();
			surftool.Begin(Godot.Mesh.PrimitiveType.Triangles);

			int n = 128; // number of circle segments
			int m = 3; // number of thickness segments

			float step = Mathf.Tau / n;

			for (int j = 0; j < n; ++j)
			{
				Basis basis = new(_ivec, j * step);
				Vector3 vertex = basis.Xform(_ivec2 * Gizmos.GizmoCircleSize);

				for (int k = 0; k < m; ++k)
				{
					Vector2 ofs = new(Mathf.Cos(Mathf.Tau * k / m), Mathf.Sin(Mathf.Tau * k / m));
					Vector3 normal = _ivec * ofs.X + _ivec2 * ofs.Y;

					surftool.SetNormal(basis.Xform(normal));
					surftool.AddVertex(vertex);
				}
			}

			for (int j = 0; j < n; ++j)
			{
				for (int k = 0; k < m; ++k)
				{
					int currentRing = j * m;
					int nextRing = (j + 1) % n * m;
					int currentSegment = k;
					int nextSegment = (k + 1) % m;

					surftool.AddIndex(currentRing + nextSegment);
					surftool.AddIndex(currentRing + currentSegment);
					surftool.AddIndex(nextRing + currentSegment);

					surftool.AddIndex(nextRing + currentSegment);
					surftool.AddIndex(nextRing + nextSegment);
					surftool.AddIndex(currentRing + nextSegment);
				}
			}

			Godot.Collections.Array arrays = surftool.CommitToArrays();

			_rotateGizmo[i].AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
			_rotateGizmo[i].SurfaceSetMaterial(0, rotateMaterial);

			if (i == 2)
			{
				ShaderMaterial borderMaterial = new()
				{
					RenderPriority = (int)Godot.Material.RenderPriorityMax,
					Shader = _rotateBorderShader,
				};
				borderMaterial.SetShaderParameter("albedo", new Color(1f, 1f, 1f, 0.2f));

				ArrayMesh borderMesh = new();
				borderMesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
				borderMesh.SurfaceSetMaterial(0, borderMaterial);

				_rotateGizmo[3] = borderMesh;
			}
		}
	}

	private void CreateArcOverlay()
	{
		StandardMaterial3D mat = new()
		{
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			NoDepthTest = true,
			RenderPriority = (int)Godot.Material.RenderPriorityMax,
			VertexColorUseAsAlbedo = true
		};

		_arcInstance = new MeshInstance3D
		{
			Mesh = _arcMesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false,
			Layers = 1 << 6,
			MaterialOverride = mat
		};

		AddChild(_arcInstance);
	}

	private void CreateInstances()
	{
		for (int i = 0; i < 4; i++)
		{
			_rotateGizmoInstance[i] = new MeshInstance3D
			{
				Mesh = _rotateGizmo[i],
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				Visible = false,
				// not using 1 because of decal wrapping onto gizmos
				Layers = 1 << 6
			};
			AddChild(_rotateGizmoInstance[i]);
		}
	}

	private void ClearInstances()
	{
		for (int i = 0; i < 3; i++)
		{
			_rotateGizmoInstance[i].QueueFree();
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
				if (_currentAxis == RotateGizmoAxis.None) return;
				if (!Visible) return;
				_dragPivot = GetPivot();
				_startRayOrigin = rayOrigin;
				_startRayNormal = rayNormal;
				_lastIntersection = null;
				_totalRotationAngle = 0f;

				Vector3 globalAxis = GetGlobalAxis(_currentAxis, _dragPivot);
				Plane startPlane = new(globalAxis, _dragPivot.Origin);
				Vector3? startHit = startPlane.IntersectsRay(rayOrigin, rayNormal);
				_arcStartDirection = startHit.HasValue
					? (startHit.Value - _dragPivot.Origin).Normalized()
					: GetPerpendicularVector(globalAxis);

				DragStarted?.Invoke();
				_isMouseDragging = true;
			}
			else
			{
				if (_isMouseDragging)
				{
					DragEnded?.Invoke();
					_isMouseDragging = false;
					_arcInstance.Visible = false;
					_arcMesh.ClearSurfaces();
				}
			}
		}
		else if (@event is InputEventMouseMotion)
		{
			if (!Visible) return;
			if (_isMouseDragging)
			{
				if (_currentAxis != RotateGizmoAxis.None)
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

	private static Vector3 GetPerpendicularVector(Vector3 v)
	{
		Vector3 aux = Mathf.Abs(v.Dot(Vector3.Up)) < 0.9f ? Vector3.Up : Vector3.Right;
		return v.Cross(aux).Normalized();
	}

	private static Vector3 GetGlobalAxis(RotateGizmoAxis axis, Transform3D pivot)
	{
		return axis switch
		{
			RotateGizmoAxis.RotateX => pivot.Basis.Xform(Vector3.Right).Normalized(),
			RotateGizmoAxis.RotateY => pivot.Basis.Xform(Vector3.Up).Normalized(),
			RotateGizmoAxis.RotateZ => pivot.Basis.Xform(Vector3.Back).Normalized(),
			_ => Vector3.Up
		};
	}

	private void RedrawArc()
	{
		if (_currentAxis == RotateGizmoAxis.None) return;

		_arcMesh.ClearSurfaces();

		Transform3D pivot = GetPivot();
		float scale = pivot.Origin.DistanceTo(GDCamera.GlobalPosition) * 0.12f;
		float radius = scale * Gizmos.GizmoCircleSize;

		Vector3 globalAxis = GetGlobalAxis(_currentAxis, _dragPivot);

		int axisIndex = (int)_currentAxis;
		Color axisColor = Gizmos.AxisColors[axisIndex];
		Color fillColor = new(axisColor.R, axisColor.G, axisColor.B, 0.25f);
		Color edgeColor = new(axisColor.R, axisColor.G, axisColor.B, 0.6f);

		int segments = Mathf.Max(2, (int)(Mathf.Abs(_totalRotationAngle) / Mathf.Tau * 128) + 2);
		segments = Mathf.Min(segments, 128);

		float angleStep = _totalRotationAngle / segments;

		_arcMesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Triangles);

		for (int i = 0; i < segments; i++)
		{
			float a0 = angleStep * i;
			float a1 = angleStep * (i + 1);

			Vector3 v0 = pivot.Origin;
			Vector3 v1 = pivot.Origin + new Basis(globalAxis, a0).Xform(_arcStartDirection) * radius;
			Vector3 v2 = pivot.Origin + new Basis(globalAxis, a1).Xform(_arcStartDirection) * radius;

			_arcMesh.SurfaceSetColor(fillColor);
			_arcMesh.SurfaceAddVertex(v0);
			_arcMesh.SurfaceSetColor(fillColor);
			_arcMesh.SurfaceAddVertex(v1);
			_arcMesh.SurfaceSetColor(fillColor);
			_arcMesh.SurfaceAddVertex(v2);
		}

		_arcMesh.SurfaceEnd();

		_arcMesh.SurfaceBegin(Godot.Mesh.PrimitiveType.Lines);

		_arcMesh.SurfaceSetColor(edgeColor);
		_arcMesh.SurfaceAddVertex(pivot.Origin);
		_arcMesh.SurfaceSetColor(edgeColor);
		_arcMesh.SurfaceAddVertex(pivot.Origin + _arcStartDirection * radius);

		Vector3 currentDir = new Basis(globalAxis, _totalRotationAngle).Xform(_arcStartDirection);
		_arcMesh.SurfaceSetColor(edgeColor);
		_arcMesh.SurfaceAddVertex(pivot.Origin);
		_arcMesh.SurfaceSetColor(edgeColor);
		_arcMesh.SurfaceAddVertex(pivot.Origin + currentDir * radius);

		_arcMesh.SurfaceEnd();

		_arcInstance.Visible = true;
	}

	private void RedrawGizmo()
	{
		if (Targets.Count == 0) return;
		if (!Visible) return;

		Transform3D pform = GetPivot();
		float gizmoScale = pform.Origin.DistanceTo(GDCamera.GlobalPosition) * 0.12f;
		Vector3 pScale = new(gizmoScale, gizmoScale, gizmoScale);

		for (int i = 0; i < 3; i++)
		{
			Transform3D axisTransform = new();

			if (pform.Basis.GetColumn(i).Normalized()
				.Dot(pform.Basis.GetColumn((i + 1) % 3).Normalized()) < 1f)
			{
				axisTransform = axisTransform.LookingAt(
					pform.Basis.GetColumn(i).Normalized(),
					pform.Basis.GetColumn((i + 1) % 3).Normalized()
				);
			}

			axisTransform.Basis = axisTransform.Basis.Scaled(pScale);
			axisTransform.Origin = pform.Origin;

			_rotateGizmoInstance[i].Transform = axisTransform;
		}
		_rotateGizmoInstance[3].Transform = new Transform3D(
			pform.Basis.Orthonormalized().Scaled(pScale),
			pform.Origin
		);
	}

	private void SetVisiblity()
	{
		for (int i = 0; i < 4; i++)
		{
			if (_isMouseDragging)
			{
				int activeAxis = (int)_currentAxis;
				_rotateGizmoInstance[i].Visible = Visible && (i == activeAxis);
			}
			else
			{
				_rotateGizmoInstance[i].Visible = Visible;
			}
		}
	}

	private void UpdateAxis(Vector3 rayOrigin, Vector3 rayNormal, Vector3 cameraNormal)
	{
		Transform3D pivot = GetPivot();
		_gizmoScale = pivot.Origin.DistanceTo(GDCamera.GlobalPosition) * 0.12f;

		float colD = 1e20f;
		int colAxis = -1;

		float rayLength = pivot.Origin.DistanceTo(rayOrigin) + Gizmos.GizmoCircleSize * _gizmoScale * 4f;
		Vector3[] result = Geometry3D.SegmentIntersectsSphere(rayOrigin, rayOrigin + rayNormal * rayLength, pivot.Origin, _gizmoScale * Gizmos.GizmoCircleSize);

		if (result.Length == 2)
		{
			Vector3 hitPosition = result[0];
			Vector3 hitNormal = result[1];

			if (hitNormal.Dot(cameraNormal) < 0.05f)
			{
				hitPosition = pivot.XformInv(hitPosition).Abs();
				int minAxis = (int)hitPosition.MinAxisIndex();

				if (hitPosition[minAxis] < _gizmoScale * GizmoRingHalfWidth)
				{
					colAxis = minAxis;
				}
			}
		}

		if (colAxis == -1)
		{
			for (int i = 0; i < 3; i++)
			{
				Plane plane = new(pivot.Basis.GetColumn(i).Normalized(), pivot.Origin);
				Vector3? result2 = plane.IntersectsRay(rayOrigin, rayNormal);

				if (result2 == null)
				{
					continue;
				}

				float dist = result2.Value.DistanceTo(pivot.Origin);
				Vector3 rDir = (result2.Value - pivot.Origin).Normalized();

				if (cameraNormal.Dot(rDir) <= 0.005f)
				{
					if (dist > _gizmoScale * (Gizmos.GizmoCircleSize - GizmoRingHalfWidth) && dist < _gizmoScale * (Gizmos.GizmoCircleSize + GizmoRingHalfWidth))
					{
						float d = rayOrigin.DistanceTo(result2.Value);

						if (d < colD)
						{
							colD = d;
							colAxis = i;
						}
					}
				}
			}
		}

		HighlightAxis(colAxis);
	}

	private void HighlightAxis(int axis)
	{
		for (int i = 0; i < 3; i++)
		{
			_rotateGizmo[i].SurfaceSetMaterial(0, i == axis ? _rotateHoverColor[i] : _rotateColor[i]);
		}

		_currentAxis = (RotateGizmoAxis)axis;
		if (RootGizmos != null)
		{
			if (_currentAxis != RotateGizmoAxis.None)
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
		Plane plane = new(cameraNormal, _dragPivot.Origin);

		Vector3 localAxis;
		switch (_currentAxis)
		{
			case RotateGizmoAxis.RotateX: localAxis = Vector3.Right; break;
			case RotateGizmoAxis.RotateY: localAxis = Vector3.Up; break;
			case RotateGizmoAxis.RotateZ: localAxis = Vector3.Back; break;
			default: return;
		}

		Vector3 globalAxis = _dragPivot.Basis.Xform(localAxis).Normalized();

		Vector3? intersection = plane.IntersectsRay(rayOrigin, rayNormal);
		if (intersection == null) return;

		if (_lastIntersection == null)
		{
			_lastIntersection = intersection;
			return;
		}

		float angle;
		float orthogonalThreshold = Mathf.Cos(Mathf.DegToRad(87));
		bool axisIsOrthogonal = Mathf.Abs(plane.Normal.Dot(globalAxis)) < orthogonalThreshold;

		if (axisIsOrthogonal)
		{
			Vector3 projectionAxis = plane.Normal.Cross(globalAxis);
			Vector3 delta = intersection.Value - _lastIntersection.Value;
			float projection = delta.Dot(projectionAxis);
			angle = projection * (Mathf.Pi / 2.0f) / (_gizmoScale * Gizmos.GizmoCircleSize);
		}
		else
		{
			Vector3 clickAxis = (_lastIntersection.Value - _dragPivot.Origin).Normalized();
			Vector3 currentAxis = (intersection.Value - _dragPivot.Origin).Normalized();
			angle = clickAxis.SignedAngleTo(currentAxis, globalAxis);
		}

		_dragPivot.Origin = Gizmos.GetCenterPivot([.. Targets]).Origin;
		_lastIntersection = intersection;

		Basis rotation = new(globalAxis, angle);

		Dragged?.Invoke(rotation);
	}
}
