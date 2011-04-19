require 'albacore'

CONFIGURATION = ENV['config'] || :Release
SOLUTION = 'src/SlimThreading.sln'
STAGE_DIR = File.expand_path("build")

task :default => [:build]

desc "Prepares the working directory for a new build"
task :clean do
  clean_dir STAGE_DIR
end

desc "Compiles the library"
msbuild :compile => [:clean] do |msb|
  msb.properties :configuration => CONFIGURATION
  msb.targets :Clean, :Build
  msb.solution = SOLUTION
end

desc 'Runs the tests'
task :test

desc "Compiles and runs the tests"
task :build => [:compile, :test] do
  Dir.glob "src/SlimThreading/bin/#{CONFIGURATION}/*.{dll,pdb,xml}" do |f|
     copy f, STAGE_DIR if File.file? f
  end
  copy 'LICENSE.txt', STAGE_DIR
  copy 'README.md', STAGE_DIR
end

def clean_dir(dir)
  Dir[dir + "/*"].each do |file|
    if Dir.exists?(file)
      clean_dir file
      Dir.delete file
    else
      File.delete file
    end
  end
  Dir.mkdir dir unless File.exist? dir
end