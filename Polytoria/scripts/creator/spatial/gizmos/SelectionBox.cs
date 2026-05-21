// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Datamodel;
using Polytoria.Datamodel.Creator;
using Polytoria.Utils;

namespace Polytoria.Creator.Spatial;

public partial class SelectionBox : Node
{
	private Dynamic? _target;
	public Gizmos? RootGizmos { get; set; }
	public World Root = null!;
	public Dynamic? Target
	{
		get => _target;
		set
		{
			GenerateBoxes();
			if (_target != value)
			{
				_target?.TransformChanged -= UpdateBox;
				_target = value;
				UpdateBox();
				_target?.TransformChanged += UpdateBox;
			}
		}
	}
	public Color SelectionColor = new(1f, 0.5f, 0f);

	private MeshInstance3D _selectionBoxMesh = null!;
	private MeshInstance3D _selectionBoxXrayMesh = null!;
	private MeshInstance3D _centerCircleMesh = null!;
	private MeshInstance3D _centerCircleXrayMesh = null!;

	private ArrayMesh _selectionBox = null!;
	private ArrayMesh _selectionBoxXray = null!;
	private ArrayMesh _centerCircle = null!;
	private ArrayMesh _centerCircleXray = null!;

	private float _gizmoScale;
	private Camera3D _camera = null!;

	private StandardMaterial3D _mat = null!;
	private StandardMaterial3D _matXray = null!;
	private StandardMaterial3D _circleMat = null!;
	private StandardMaterial3D _circleMatXray = null!;

	private Aabb? _cachedGlobalBounds = null;
	private Vector3 _cachedTargetPosition;

	private bool _boxGenerated = false;

	public bool ShowCenterCircle { get; set; } = true;
	private const float CircleRadius = 0.005f;
	private const int CircleSegments = 32;

	public override void _EnterTree()
	{
		GenerateBoxes();
		UpdateBox();
		base._EnterTree();
	}

	public override void _ExitTree()
	{
		_selectionBoxMesh?.QueueFree();
		_selectionBoxXrayMesh?.QueueFree();
		_centerCircleMesh?.QueueFree();
		_centerCircleXrayMesh?.QueueFree();
		base._ExitTree();
	}

	public override void _Ready()
	{
		_camera = GetViewport().GetCamera3D();
	}

	public override void _Process(double delta)
	{
		if (Target == null || _camera == null) return;

		Vector3 center = _cachedGlobalBounds.HasValue
			? _cachedGlobalBounds.Value.GetCenter()
			: Target.GetGlobalPosition();

		Transform3D circleXform = BuildBillboardTransform(center);

		_centerCircleMesh.GlobalTransform = circleXform;
		_centerCircleXrayMesh.GlobalTransform = circleXform;
	}

	private Transform3D BuildBillboardTransform(Vector3 center)
	{
		float distance = _camera.GlobalTransform.Origin.DistanceTo(center);
		Basis billboardBasis = _camera.GlobalTransform.Basis.Scaled(Vector3.One * distance);
		return new Transform3D(billboardBasis, center);
	}

	private void GenerateBoxes()
	{
		if (_boxGenerated) return;
		_boxGenerated = true;
		Aabb aabb = new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1, 1, 1));

		SurfaceTool st = new();
		SurfaceTool stXray = new();

		st.Begin(Godot.Mesh.PrimitiveType.Lines);
		stXray.Begin(Godot.Mesh.PrimitiveType.Lines);

		for (int i = 0; i < 12; i++)
		{
			aabb.GetEdge(i, out Vector3 a, out Vector3 b);

			st.AddVertex(a);
			st.AddVertex(b);
			stXray.AddVertex(a);
			stXray.AddVertex(b);
		}

		_mat = new()
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha
		};
		st.SetMaterial(_mat);
		_selectionBox = st.Commit();

		_matXray = new()
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			NoDepthTest = true,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha
		};
		stXray.SetMaterial(_matXray);
		_selectionBoxXray = stXray.Commit();

		_selectionBoxMesh = new MeshInstance3D { Mesh = _selectionBox };
		Root.GDNode.AddChild(_selectionBoxMesh, @internal: Node.InternalMode.Back);

		_selectionBoxXrayMesh = new MeshInstance3D { Mesh = _selectionBoxXray };
		Root.GDNode.AddChild(_selectionBoxXrayMesh, @internal: Node.InternalMode.Back);

		GenerateCenterCircle();
	}

	private void GenerateCenterCircle()
	{
		SurfaceTool stCircle = new();
		SurfaceTool stCircleXray = new();

		stCircle.Begin(Godot.Mesh.PrimitiveType.Triangles);
		stCircleXray.Begin(Godot.Mesh.PrimitiveType.Triangles);

		Vector3 origin = Vector3.Zero;

		for (int i = 0; i < CircleSegments; i++)
		{
			float angleA = Mathf.Tau * i / CircleSegments;
			float angleB = Mathf.Tau * (i + 1) / CircleSegments;

			Vector3 a = new(Mathf.Cos(angleA) * CircleRadius, Mathf.Sin(angleA) * CircleRadius, 0f);
			Vector3 b = new(Mathf.Cos(angleB) * CircleRadius, Mathf.Sin(angleB) * CircleRadius, 0f);

			stCircle.AddVertex(origin);
			stCircle.AddVertex(a);
			stCircle.AddVertex(b);

			stCircleXray.AddVertex(origin);
			stCircleXray.AddVertex(a);
			stCircleXray.AddVertex(b);
		}

		_circleMat = new()
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		stCircle.SetMaterial(_circleMat);
		_centerCircle = stCircle.Commit();

		_circleMatXray = new()
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			NoDepthTest = true,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled
		};
		stCircleXray.SetMaterial(_circleMatXray);
		_centerCircleXray = stCircleXray.Commit();

		_centerCircleMesh = new MeshInstance3D { Mesh = _centerCircle };
		Root.GDNode.AddChild(_centerCircleMesh, @internal: Node.InternalMode.Back);

		_centerCircleXrayMesh = new MeshInstance3D { Mesh = _centerCircleXray };
		Root.GDNode.AddChild(_centerCircleXrayMesh, @internal: Node.InternalMode.Back);
	}

	public void InvalidateBoundCache()
	{
		_cachedGlobalBounds = null;
	}

	public void UpdateBox()
	{
		bool visible = Target != null;

		_selectionBoxMesh.Visible = Target != null;
		_selectionBoxXrayMesh.Visible = Target != null;
		_centerCircleMesh.Visible = visible && ShowCenterCircle;
		_centerCircleXrayMesh.Visible = visible && ShowCenterCircle;

		if (Target == null) return;

		var toolMode = CreatorService.Interface.ToolMode;
		Aabb globalBounds;
		bool isDragging = RootGizmos != null && RootGizmos.HoveringGizmos && (toolMode == ToolModeEnum.Move || toolMode == ToolModeEnum.Select);

		if (isDragging && _cachedGlobalBounds.HasValue)
		{
			// Fast path: offset the cached bounds
			Vector3 currentPosition = Target.GetGlobalPosition();
			Vector3 positionDelta = currentPosition - _cachedTargetPosition;

			globalBounds = new Aabb(
				_cachedGlobalBounds.Value.Position + positionDelta,
				_cachedGlobalBounds.Value.Size
			);

			_cachedGlobalBounds = globalBounds;
			_cachedTargetPosition = currentPosition;
		}
		else
		{
			// Full recalculation
			globalBounds = Target.CalculateBounds();
			_cachedGlobalBounds = globalBounds;
			_cachedTargetPosition = Target.GetGlobalPosition();
		}

		Vector3 size = globalBounds.Size + Vector3.One * 0.005f;
		Vector3 center = globalBounds.GetCenter();

		Transform3D boxXform = new(
			Basis.FromScale(size),
			center
		);

		_mat.AlbedoColor = SelectionColor;
		_matXray.AlbedoColor = SelectionColor * new Color(1f, 1f, 1f, 0.2f);

		_selectionBoxMesh.GlobalTransform = boxXform;
		_selectionBoxXrayMesh.GlobalTransform = boxXform;

		_circleMat.AlbedoColor = SelectionColor;
		_circleMatXray.AlbedoColor = SelectionColor * new Color(1f, 1f, 1f, 0.2f);

		_centerCircleMesh.GlobalTransform = boxXform;
		_centerCircleXrayMesh.GlobalTransform = boxXform;
	}
}
