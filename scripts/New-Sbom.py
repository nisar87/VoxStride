"""Build an SPDX 2.3 JSON SBOM from `dotnet list package --include-transitive --format json` output."""
import json, sys, uuid, datetime
src, version, out = sys.argv[1], sys.argv[2], sys.argv[3]
data = json.load(open(src, encoding="utf-8-sig"))
pkgs = {}
for proj in data.get("projects", []):
    for fw in proj.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for p in fw.get(kind, []):
                v = p.get("resolvedVersion") or p.get("requestedVersion")
                pkgs[(p["id"], v)] = None
root = "SPDXRef-VoxStride"
doc = {
    "spdxVersion": "SPDX-2.3", "dataLicense": "CC0-1.0", "SPDXID": "SPDXRef-DOCUMENT",
    "name": f"VoxStride-{version}",
    "documentNamespace": f"https://github.com/nisar87/VoxStride/sbom/{version}-{uuid.uuid4()}",
    "creationInfo": {"created": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "creators": ["Tool: VoxStride New-Sbom.py"]},
    "packages": [{"name": "VoxStride", "SPDXID": root, "versionInfo": version, "downloadLocation": "https://github.com/nisar87/VoxStride",
                  "filesAnalyzed": False, "licenseConcluded": "LicenseRef-VoxStride-Proprietary", "licenseDeclared": "LicenseRef-VoxStride-Proprietary", "copyrightText": "Copyright (c) 2026 nisar87"}],
    "relationships": [{"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": root}],
    "hasExtractedLicensingInfos": [{"licenseId": "LicenseRef-VoxStride-Proprietary", "extractedText": "See LICENSE", "name": "VoxStride proprietary"}],
}
for i, (name, v) in enumerate(sorted(pkgs, key=lambda k: (k[0].lower(), k[1] or ""))):
    sid = f"SPDXRef-Package-{i}"
    doc["packages"].append({"name": name, "SPDXID": sid, "versionInfo": v, "downloadLocation": f"https://www.nuget.org/packages/{name}/{v}",
        "filesAnalyzed": False, "licenseConcluded": "NOASSERTION", "licenseDeclared": "NOASSERTION", "copyrightText": "NOASSERTION",
        "externalRefs": [{"referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl", "referenceLocator": f"pkg:nuget/{name}@{v}"}]})
    doc["relationships"].append({"spdxElementId": root, "relationshipType": "DEPENDS_ON", "relatedSpdxElement": sid})
json.dump(doc, open(out, "w", encoding="utf-8"), indent=1)
print(f"SBOM: {len(pkgs)} packages -> {out}")
