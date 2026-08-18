# Local inference catalog provenance

The supported hardware, runtime, model, and recipe selections in the local
inference catalog incorporate data adapted from the NVIDIA CAIR recipe catalog,
copyright NVIDIA Corporation and licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

Changes made for OpenClaw include representing recipe data as typed Windows
records, limiting selection to explicitly supported hardware, and replacing
mutable artifact references with independently verified public GitHub and
Hugging Face revision and SHA-256 pins. A public CAIR source URL is expected but
was not available when these records were prepared. No non-public location is
recorded in source or generated state.
