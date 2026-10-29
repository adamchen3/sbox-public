namespace Sandbox.ResourceDecompiler;

public static class DecompilerFormats
{
	// Comment out an extension to disable scanning it. Add custom GameResource extensions here.
	public static IReadOnlySet<string> Enabled { get; } = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
	{
		".scene_c",
		".prefab_c",
		".sound_c",
		".tmat_c",
		".sprite_c",
		".decal_c",
		".surface_c",
		".terrain_c",
		".clutter_c",
		".navarea_c",
		".sndscape_c",
		".rtex_c",

		".vtex_c",
		".vsnd_c",
		".shader_c",
		".vmat_c",
		".vmdl_c",
		".vanmgrph_c",

		// Runtime DATA salvage only; editable native source reconstruction is not verified.
		".vpcf_c",
		".vpost_c",
		".vsnap_c",
	};
}
