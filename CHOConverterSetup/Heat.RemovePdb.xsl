<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
  xmlns:xsl="http://www.w3.org/1999/XSL/Transform">

  <xsl:output method="xml" indent="yes" />

  <!-- Identity transform -->
  <xsl:template match="@*|node()">
    <xsl:copy>
      <xsl:apply-templates select="@*|node()" />
    </xsl:copy>
  </xsl:template>

  <!-- Drop any Component that contains a File whose Source contains .pdb (case-insensitive). -->
  <xsl:template match="*[local-name()='Component'][*[local-name()='File'][contains(translate(@Source,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), '.pdb')]]" />

  <!-- Drop any File nodes containing .pdb (just in case a component contains multiple files). -->
  <xsl:template match="*[local-name()='File'][contains(translate(@Source,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), '.pdb')]" />

</xsl:stylesheet>
