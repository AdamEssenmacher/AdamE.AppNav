plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.adamessenmacher.appnav.probe"
    compileSdk = 36

    defaultConfig {
        minSdk = 28
    }

    buildFeatures {
        compose = true
    }

    testOptions.unitTests.isIncludeAndroidResources = true

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    sourceSets.named("main") {
        kotlin.srcDir(rootProject.file("../../../src/AdamE.AppNav.Compose/src/main/kotlin"))
    }
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

val nugetPackages = providers.environmentVariable("NUGET_PACKAGES")
    .orElse("${System.getProperty("user.home")}/.nuget/packages")
val oahuAndroid = nugetPackages.map {
    "$it/oahu.android/0.1.0-preview.g7a56b3ba/lib/net11.0-android37.0"
}

dependencies {
    // The .NET app packages these two AARs through Oahu.Android.
    compileOnly(files(oahuAndroid.map { "$it/oahu-runtime-release.aar" }))
    compileOnly(files(oahuAndroid.map { "$it/oahu-compose-release.aar" }))

    implementation("androidx.compose.animation:animation:1.11.3")
    implementation("androidx.compose.foundation:foundation:1.11.3")
    implementation("androidx.compose.material3:material3:1.4.0")
    implementation("androidx.navigation3:navigation3-ui:1.2.1")
    implementation("androidx.navigation3:navigation3-runtime:1.2.1")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.11.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.robolectric:robolectric:4.17")
    testImplementation("androidx.compose.ui:ui-test-junit4:1.11.3")
    testImplementation(files(oahuAndroid.map { "$it/oahu-runtime-release.aar" }))
    testImplementation(files(oahuAndroid.map { "$it/oahu-compose-release.aar" }))
    debugImplementation("androidx.compose.ui:ui-test-manifest:1.11.3")
}
