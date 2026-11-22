// The cloud shaders' test and measuring switches (owner 2026-11-22: the product holds no test code). A switch that only probes, the weather player or the viewer change
// (aid pictures, the cost attribution, the parity test cell, the noise blend's alternative ...) is written DEV(uniform, product value). Without the variant CLOUD_DEV the
// product value is a constant and fxc removes the test branch with it; CloudDevVariant.cs turns the variant on where a material holds a value other than the product's, and its
// table (CloudDevVariant.Layer, .Shadow) is the one place the product values are written on the C# side (CloudDevVariantTests keeps the two equal).
#ifndef REDEFINITION_CLOUD_DEV
#define REDEFINITION_CLOUD_DEV

#if defined(CLOUD_DEV)
#define DEV(value, product) (value)
#else
#define DEV(value, product) (product)
#endif

#endif
