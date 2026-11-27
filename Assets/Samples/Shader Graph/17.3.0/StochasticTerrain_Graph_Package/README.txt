STOCHASTIC TERRAIN - UNITY 6.3 URP

File:
- StochasticTerrain.shadergraph

Basis:
- Based on Unity's Terrain Shaders sample: CNM Hex Height.
- Uses hexagonal tile breakup and painted Terrain Layer splatmap blending.
- The graph itself is a renamed copy of the sample graph, not a newly authored graph.

INSTALL:
1. Make sure the imported Unity Terrain Shaders sample remains inside your project's Assets folder.
2. Copy StochasticTerrain.shadergraph and its .meta file into:
   Assets/Samples/Shader Graph/<your Shader Graph version>/Terrain Shaders/Shaders/
   (or another folder inside Assets; the imported Terrain Shaders Subgraphs folder must remain in the project).
3. Let Unity reimport the graph.
4. Create a Material using Shader Graphs/StochasticTerrain (shader name may appear after import).
5. Assign that material to Terrain > Settings > Material.
6. Paint using Terrain Layers as usual.

IMPORTANT:
- This graph depends on the Layer Hex and Layer Hex Detail Sub Graph assets from the imported Terrain Shaders sample.
- Keep the sample's Subgraphs folder in Assets. Do not delete or move those dependencies.
- This CNM variant expects standard Terrain Layer texture inputs (Diffuse, Normal, Mask) and uses painted splatmap blending.
- The Hex breakup version is relatively expensive: Unity's sample documentation lists 37 texture samples and relative cost 4.08 compared with Standard Terrain Lit = 1.0. Test on target Android devices.
- If Android performance is too costly, use the sample's CNM Rotation Height graph instead (less effective breakup but cheaper).
